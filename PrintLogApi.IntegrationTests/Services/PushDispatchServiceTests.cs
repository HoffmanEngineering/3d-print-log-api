using Microsoft.Extensions.Logging.Abstractions;
using PrintLogApi.Models;
using PrintLogApi.Services;
using PrintLogApi.Services.Push;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

public class RecordingFcmClient : IFcmClient
{
    public List<FcmMessage> Sent { get; } = [];
    public IReadOnlyList<string> UnregisteredToReturn { get; set; } = [];
    public Exception? ThrowOnSend { get; set; }

    public Task<FcmSendResult> SendAsync(IReadOnlyList<FcmMessage> messages, CancellationToken ct)
    {
        if (ThrowOnSend is not null)
        {
            throw ThrowOnSend;
        }

        Sent.AddRange(messages);
        return Task.FromResult(new FcmSendResult(UnregisteredToReturn, messages.Count, 0));
    }
}

public class PushDispatchServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PushDispatchServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    // PrintId is a real foreign key, so it has to point at a seeded print.
    private static Notification NotificationOfType(NotificationType type) => new()
    {
        Id = Guid.NewGuid(),
        UserId = IntegrationTestSeeder.TestUserId,
        Type = type,
        Title = "Print failed",
        Message = "Your print \"Benchy\" has failed",
        PrintId = IntegrationTestSeeder.TestPrintId,
        CreatedDate = DateTime.UtcNow
    };

    private (IPushDispatchService Service, IDeviceTokenService Tokens, PrintLogContext Db, IServiceScope Scope)
        Build(IFcmClient fcm)
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var tokens = scope.ServiceProvider.GetRequiredService<IDeviceTokenService>();
        var service = new PushDispatchService(db, tokens, fcm, NullLogger<PushDispatchService>.Instance);
        return (service, tokens, db, scope);
    }

    [Fact]
    public async Task DispatchesForEligibleType()
    {
        var fcm = new RecordingFcmClient();
        var (service, tokens, _, scope) = Build(fcm);
        using var _s = scope;

        // Other tests in this class register devices for the same user against the same
        // database, so clear first: "exactly one message" is only meaningful with a known
        // device count.
        await tokens.PruneTokens(await tokens.GetTokensForUser(IntegrationTestSeeder.TestUserId));
        await tokens.RegisterDevice(IntegrationTestSeeder.TestUserId, $"tok-{Guid.NewGuid():N}", DevicePlatform.Android, null);

        await service.DispatchForNotification(NotificationOfType(NotificationType.PrintFailed), TestContext.Current.CancellationToken);

        Assert.Single(fcm.Sent);
        Assert.Equal("Print failed", fcm.Sent[0].Title);
        Assert.Equal(IntegrationTestSeeder.TestPrintId.ToString(), fcm.Sent[0].Data["printId"]);
        Assert.False(fcm.Sent[0].Data.ContainsKey("actionUrl"));
    }

    [Fact]
    public async Task DoesNotDispatchForIneligibleType()
    {
        var fcm = new RecordingFcmClient();
        var (service, tokens, _, scope) = Build(fcm);
        using var _s = scope;
        await tokens.RegisterDevice(IntegrationTestSeeder.TestUserId, $"tok-{Guid.NewGuid():N}", DevicePlatform.Android, null);

        await service.DispatchForNotification(NotificationOfType(NotificationType.Comment), TestContext.Current.CancellationToken);

        Assert.Empty(fcm.Sent);
    }

    [Fact]
    public async Task DoesNotDispatchWhenUserHasNoDevices()
    {
        var fcm = new RecordingFcmClient();
        var (service, _, _, scope) = Build(fcm);
        using var _s = scope;

        // The secondary account has no registered devices, and unlike the primary one it is
        // not shared with the other tests in this class.
        var notification = NotificationOfType(NotificationType.PrintFailed);
        notification.UserId = IntegrationTestSeeder.SecondaryUserId;

        await service.DispatchForNotification(notification, TestContext.Current.CancellationToken);

        Assert.Empty(fcm.Sent);
    }

    [Fact]
    public async Task RespectsDisabledPreference()
    {
        var fcm = new RecordingFcmClient();
        var (service, tokens, db, scope) = Build(fcm);
        using var _s = scope;
        await tokens.RegisterDevice(IntegrationTestSeeder.SecondaryUserId, $"tok-{Guid.NewGuid():N}", DevicePlatform.Android, null);

        db.UserSettings.Add(new UserSetting
        {
            UserId = IntegrationTestSeeder.SecondaryUserId,
            UserSettingTypeId = 16, // Push_PrintFailed
            Value = "false",
            CreatedDate = DateTime.UtcNow,
            UpdatedDate = DateTime.UtcNow,
            CreatedById = IntegrationTestSeeder.SecondaryUserId,
            UpdatedById = IntegrationTestSeeder.SecondaryUserId
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var notification = NotificationOfType(NotificationType.PrintFailed);
        notification.UserId = IntegrationTestSeeder.SecondaryUserId;

        await service.DispatchForNotification(notification, TestContext.Current.CancellationToken);

        Assert.Empty(fcm.Sent);
    }

    [Fact]
    public async Task PrunesUnregisteredTokens()
    {
        var token = $"tok-{Guid.NewGuid():N}";
        var fcm = new RecordingFcmClient { UnregisteredToReturn = [token] };
        var (service, tokens, db, scope) = Build(fcm);
        using var _s = scope;
        await tokens.RegisterDevice(IntegrationTestSeeder.TestUserId, token, DevicePlatform.Android, null);

        await service.DispatchForNotification(NotificationOfType(NotificationType.PrintFailed), TestContext.Current.CancellationToken);

        Assert.Empty(db.DeviceTokens.Where(d => d.Token == token));
    }

    [Fact]
    public async Task SendFailureDoesNotThrow()
    {
        var fcm = new RecordingFcmClient { ThrowOnSend = new InvalidOperationException("FCM down") };
        var (service, tokens, _, scope) = Build(fcm);
        using var _s = scope;
        await tokens.RegisterDevice(IntegrationTestSeeder.TestUserId, $"tok-{Guid.NewGuid():N}", DevicePlatform.Android, null);

        var ex = await Record.ExceptionAsync(() =>
            service.DispatchForNotification(NotificationOfType(NotificationType.PrintFailed), TestContext.Current.CancellationToken));

        Assert.Null(ex);
    }

    /// <summary>
    /// AndroidNotification.EventTimestamp is non-nullable, so leaving it unset ships
    /// event_time as 0001-01-01 and Android renders the card's age from it — "2023y" on the
    /// notification shade instead of "now". The dispatch layer therefore has to carry the
    /// notification's own timestamp down to the client.
    /// </summary>
    [Fact]
    public async Task DispatchCarriesTheNotificationTimestamp()
    {
        var fcm = new RecordingFcmClient();
        var (service, tokens, _, scope) = Build(fcm);
        using var _s = scope;

        await tokens.PruneTokens(await tokens.GetTokensForUser(IntegrationTestSeeder.TestUserId));
        await tokens.RegisterDevice(IntegrationTestSeeder.TestUserId, $"tok-{Guid.NewGuid():N}", DevicePlatform.Android, null);

        var created = new DateTime(2026, 8, 29, 21, 23, 41, DateTimeKind.Utc);
        var notification = NotificationOfType(NotificationType.PrintCompleted);
        notification.CreatedDate = created;

        await service.DispatchForNotification(notification, TestContext.Current.CancellationToken);

        var sent = Assert.Single(fcm.Sent);
        Assert.Equal(new DateTimeOffset(created), sent.EventTime);
    }

    /// <summary>
    /// The column round-trips out of SQL Server as Unspecified even though the value stored
    /// is UTC, so the kind has to be reasserted or the instant shifts by the server's zone.
    /// </summary>
    [Fact]
    public async Task DispatchTreatsAnUnspecifiedTimestampAsUtc()
    {
        var fcm = new RecordingFcmClient();
        var (service, tokens, _, scope) = Build(fcm);
        using var _s = scope;

        await tokens.PruneTokens(await tokens.GetTokensForUser(IntegrationTestSeeder.TestUserId));
        await tokens.RegisterDevice(IntegrationTestSeeder.TestUserId, $"tok-{Guid.NewGuid():N}", DevicePlatform.Android, null);

        var notification = NotificationOfType(NotificationType.PrintCompleted);
        notification.CreatedDate = new DateTime(2026, 8, 29, 21, 23, 41, DateTimeKind.Unspecified);

        await service.DispatchForNotification(notification, TestContext.Current.CancellationToken);

        var sent = Assert.Single(fcm.Sent);
        Assert.Equal(TimeSpan.Zero, sent.EventTime.Offset);
        Assert.Equal(21, sent.EventTime.Hour);
    }
}
