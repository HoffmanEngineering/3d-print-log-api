using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Achievements;
using PrintLogApi.Models;
using PrintLogApi.Profiles;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Achievements;

public class AchievementNotificationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AchievementNotificationTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AchievementDefinition Def(string key) => AchievementCatalog.Find(key)!;

    [Fact]
    public void ForGrant_TieredMessage()
    {
        var n = AchievementNotificationFactory.ForGrant(7, Def("prints-logged"), 3, retroactive: false);

        Assert.Equal(NotificationType.Achievement, n.Type);
        Assert.Equal("Achievement unlocked: Prolific Printer", n.Title);
        Assert.Equal("Gold Silk · You've logged 50 prints.", n.Message);
        Assert.Equal("/achievements?badge=prints-logged", n.ActionUrl);
        Assert.Equal("""{"v":1,"key":"prints-logged","tier":3}""", n.Metadata);
        Assert.False(n.IsRead);
        Assert.Equal(7, n.UserId);
    }

    [Fact]
    public void ForGrant_OneTimeMessage()
    {
        var n = AchievementNotificationFactory.ForGrant(7, Def("first-print"), 1, retroactive: false);

        Assert.Equal("You logged your first print.", n.Message);
    }

    [Fact]
    public void ForGrant_RetroactiveIsRead()
    {
        var n = AchievementNotificationFactory.ForGrant(7, Def("first-print"), 1, retroactive: true);

        Assert.True(n.IsRead);
        Assert.NotNull(n.ReadDate);
    }

    [Fact]
    public void Summary_IsUnreadWithCount()
    {
        var n = AchievementNotificationFactory.Summary(7, 12);

        Assert.Equal("You've earned 12 achievements", n.Title);
        Assert.Equal("""{"v":1,"summary":true,"count":12}""", n.Metadata);
        Assert.False(n.IsRead);
    }

    [Theory]
    [InlineData("""{"v":1,"key":"first-print","tier":1}""", "first-print", 1, false, null)]
    [InlineData("""{"v":1,"summary":true,"count":4}""", null, null, true, 4)]
    public void SummaryDto_ParsesMetadata(string metadata, string? key, int? tier, bool summary, int? count)
    {
        var parsed = NotificationProfile.TryParseAchievement(NotificationType.Achievement, metadata)!;

        Assert.Equal(key, parsed.Key);
        Assert.Equal(tier, parsed.Tier);
        Assert.Equal(summary, parsed.Summary);
        Assert.Equal(count, parsed.Count);
    }

    [Theory]
    [InlineData(NotificationType.Achievement, "{not json")]
    [InlineData(NotificationType.Achievement, """{"v":2,"key":"first-print","tier":1}""")]
    [InlineData(NotificationType.Achievement, null)]
    [InlineData(NotificationType.Achievement, "[]")]
    [InlineData(NotificationType.PrintCompleted, """{"v":1,"key":"first-print","tier":1}""")]
    public void SummaryDto_RejectsUnparseable(NotificationType type, string? metadata)
    {
        Assert.Null(NotificationProfile.TryParseAchievement(type, metadata));
    }

    private static async Task<User> SeedNotificationsAsync(PrintLogContext db)
    {
        var user = await AchievementTestData.CreateUserAsync(db);
        db.Notifications.AddRange(
            AchievementNotificationFactory.ForGrant(user.Id, Def("first-print"), 1, retroactive: false),
            AchievementNotificationFactory.ForGrant(user.Id, Def("first-printer"), 1, retroactive: true),
            new Notification
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Type = NotificationType.SystemAnnouncement,
                Title = "Key",
                CreatedDate = DateTime.UtcNow,
            });
        await db.SaveChangesAsync(Ct);
        return user;
    }

    [Fact]
    public async Task List_FiltersByType()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var service = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var user = await SeedNotificationsAsync(db);

        var achievements = await service.GetNotificationsForUser(user.Id, new PagedRequest(), null, NotificationType.Achievement);
        var all = await service.GetNotificationsForUser(user.Id, new PagedRequest());

        Assert.Equal(2, achievements.Items.Count);
        Assert.All(achievements.Items, n => Assert.Equal(NotificationType.Achievement, n.Type));
        Assert.All(achievements.Items, n => Assert.NotNull(n.Achievement));
        Assert.Equal(3, all.Items.Count);
        Assert.Null(all.Items.Single(n => n.Type == NotificationType.SystemAnnouncement).Achievement);
    }

    [Fact]
    public async Task UnreadCount_ReturnsAchievementSubcount()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var service = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var user = await SeedNotificationsAsync(db);

        var (total, achievements) = await service.GetUnreadCountsForUser(user.Id);

        Assert.Equal(2, total);
        Assert.Equal(1, achievements);
    }

    [Fact]
    public async Task UnreadCountEndpoint_IncludesAchievementCount()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Notifications/unread-count");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"unreadAchievementCount\":", body);
    }

    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Saves { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Saves++;
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task PersistAchievementNotifications_CommitsTrackedGrantsInSameSave()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        long userId;
        {
            var seedDb = sp.GetRequiredService<PrintLogContext>();
            userId = (await AchievementTestData.CreateUserAsync(seedDb)).Id;
        }

        var counter = new SaveCounter();
        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(sp.GetRequiredService<PrintLogContext>().Database.GetDbConnection())
            .AddInterceptors(counter)
            .Options;
        await using var db = new PrintLogContext(options);
        var service = new NotificationService(db, sp.GetRequiredService<AutoMapper.IMapper>(), sp.GetRequiredService<PrintLogApi.Services.Push.IPushDispatchService>());

        db.UserAchievements.Add(new UserAchievement { UserId = userId, AchievementKey = "first-print", Tier = 1, UnlockedAt = DateTime.UtcNow });
        await service.PersistAchievementNotifications([AchievementNotificationFactory.ForGrant(userId, Def("first-print"), 1, false)], TestContext.Current.CancellationToken);

        Assert.Equal(1, counter.Saves);
        Assert.Equal(1, await db.UserAchievements.CountAsync(a => a.UserId == userId, Ct));
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == userId, Ct));
    }
}
