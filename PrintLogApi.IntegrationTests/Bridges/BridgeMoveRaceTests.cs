using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Bridges;
using PrintLogApi.Services.Bridges;
using Xunit;
using static PrintLogApi.IntegrationTests.Bridges.BridgeTestKit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// A move races other writes to the same entry (story 15a): another move, an owner's seal, a
/// check-in's credential issues. The entry's <c>IssuesStamp</c> concurrency token serializes them,
/// so none of them lands on the wrong bridge or survives the move.
/// </summary>
public class BridgeMoveRaceTests(BridgeMoveRaceTests.Factory factory) : IClassFixture<BridgeMoveRaceTests.Factory>
{
    public class Factory : CustomWebApplicationFactory
    {
        public SaveInterceptor Interceptor { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.ConfigureDbContext<PrintLogContext>(options => options.AddInterceptors(Interceptor)));
        }
    }

    /// <summary>Runs <see cref="Before"/> once, just before the next save that matches <see cref="When"/>.</summary>
    public sealed class SaveInterceptor : SaveChangesInterceptor
    {
        public Func<DbContext, bool>? When { get; set; }

        public Func<Task>? Before { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Before is { } before && When is { } when && when(eventData.Context!))
            {
                Before = null;
                When = null;
                await before();
            }

            return result;
        }
    }

    private static bool IsMove(DbContext context) => context.ChangeTracker.Entries<BridgePrinter>()
        .Any(e => e.State == EntityState.Modified && e.Property(p => p.BridgeId).IsModified);

    private static bool WritesIssues(DbContext context) => context.ChangeTracker.Entries<BridgePrinter>()
        .Any(e => e.State == EntityState.Modified && e.Property(p => p.CredentialIssues).IsModified && !e.Property(p => p.BridgeId).IsModified);

    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    private async Task<(PairedBridge A, PairedBridge B, PairedBridge C)> ThreeBridgesAsync(string label, string? aKey = null, string? bKey = null)
    {
        var a = await PairBridgeAsync(_client, label);
        await CheckInOkAsync(_client, a.Token, new { publicKey = aKey, networks = new[] { "192.168.1.0/24" } });
        var others = new List<PairedBridge>();
        foreach (var name in new[] { "workshop", "garage" })
        {
            var paired = await PairAsync(_client, a.Subject, AllBridgeScopes, name: $"printlog-bridge on {name}");
            var bridge = new PairedBridge(a.Subject, paired.GetProperty("token").GetString()!, paired.GetProperty("id").GetInt64());
            await CheckInOkAsync(_client, bridge.Token, new { publicKey = others.Count == 0 ? bKey : null, networks = new[] { "192.168.2.0/24" } });
            others.Add(bridge);
        }

        return (a, others[0], others[1]);
    }

    private async Task<long> ConnectAsync(PairedBridge bridge, string kind, string host, string? serial = null)
    {
        var response = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/printers", bridge.Subject,
            new { kind, host, serial, printerId = await CreatePrinterAsync(_client, bridge.Subject) }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetInt64();
    }

    private async Task<long?> BridgeOfEntryAsync(long entryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.BridgePrinters.Where(p => p.Id == entryId).Select(p => (long?)p.BridgeId).SingleOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TwoMovesRacing_OneWins_AndTheOtherSaysWhereItWent()
    {
        var (a, b, c) = await ThreeBridgesAsync("race-moves");
        var entryId = await ConnectAsync(a, "moonraker", "192.168.1.20");
        var userId = await UserIdAsync(factory, a.Subject);

        factory.Interceptor.When = IsMove;
        factory.Interceptor.Before = async () =>
        {
            using var scope = factory.Services.CreateScope();
            var bridges = scope.ServiceProvider.GetRequiredService<IBridgeService>();
            Assert.NotNull(await bridges.MoveAsync(userId, a.GrantId, entryId, new MoveBridgePrinterDto { ToBridgeId = c.GrantId }, TestContext.Current.CancellationToken));
        };
        var loser = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{a.GrantId}/printers/{entryId}/move", a.Subject, new { toBridgeId = b.GrantId }));

        Assert.Null(factory.Interceptor.Before);
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal("printer_moved", await CodeOf(loser));
        Assert.Equal(c.GrantId, await BridgeOfEntryAsync(entryId));
        Assert.Empty((await GetAsUserAsync(_client, a.Subject, $"/api/bridges/{b.GrantId}/printers")).EnumerateArray());
    }

    /// <summary>A seal to the old bridge's key that commits during the move doesn't survive it: the move's own value for the new bridge does.</summary>
    [Fact]
    public async Task ASealDuringAMove_IsReplacedByTheMovesOwnValue()
    {
        using var key = new BridgeSealedCredentialTests.BridgeKey();
        using var bKey = new BridgeSealedCredentialTests.BridgeKey();
        var (a, b, _) = await ThreeBridgesAsync("race-seal", key.PublicKey, bKey.PublicKey);
        var entryId = await ConnectAsync(a, "bambu", "192.168.1.21", "RACE" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant());
        var userId = await UserIdAsync(factory, a.Subject);

        factory.Interceptor.When = IsMove;
        factory.Interceptor.Before = () => SealOnOldBridgeAsync(userId, a, entryId, key);
        var moved = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{a.GrantId}/printers/{entryId}/move", a.Subject, new
        {
            toBridgeId = b.GrantId,
            host = "192.168.2.21",
            keyFingerprint = bKey.Fingerprint,
            secrets = new Dictionary<string, string> { ["access_code"] = CredentialSealer.Seal(bKey.PublicKey, CredentialSealer.AssociatedData(b.GrantId, entryId, "access_code"), "12345678") },
        }));

        Assert.Null(factory.Interceptor.Before);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var stored = Assert.Single(await db.BridgePrinterSecrets.Where(s => s.BridgePrinterId == entryId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(bKey.Fingerprint, stored.KeyFingerprint);
    }

    /// <summary>A move that read the entry before the owner set its access code can't carry the printer off without it.</summary>
    [Fact]
    public async Task ASealDuringAMove_WithoutTheValue_StopsTheMove()
    {
        using var key = new BridgeSealedCredentialTests.BridgeKey();
        var (a, b, _) = await ThreeBridgesAsync("race-seal-missing", key.PublicKey);
        var entryId = await ConnectAsync(a, "bambu", "192.168.1.23", "RACE" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant());
        var userId = await UserIdAsync(factory, a.Subject);

        factory.Interceptor.When = IsMove;
        factory.Interceptor.Before = () => SealOnOldBridgeAsync(userId, a, entryId, key);
        var moved = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{a.GrantId}/printers/{entryId}/move", a.Subject, new { toBridgeId = b.GrantId, host = "192.168.2.23" }));

        Assert.Null(factory.Interceptor.Before);
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
        Assert.Equal("secrets_required", await CodeOf(moved));
        Assert.Equal(a.GrantId, await BridgeOfEntryAsync(entryId));
    }

    private async Task SealOnOldBridgeAsync(long userId, PairedBridge a, long entryId, BridgeSealedCredentialTests.BridgeKey key)
    {
        using var scope = factory.Services.CreateScope();
        var bridges = scope.ServiceProvider.GetRequiredService<IBridgeService>();
        var sealedValue = CredentialSealer.Seal(key.PublicKey, CredentialSealer.AssociatedData(a.GrantId, entryId, "access_code"), "12345678");
        Assert.NotNull(await bridges.SetSecretAsync(userId, a.GrantId, entryId, "access_code",
            new SetBridgePrinterSecretDto { Sealed = sealedValue, KeyFingerprint = key.Fingerprint }, TestContext.Current.CancellationToken));
    }

    /// <summary>The old bridge's issue report, read before the move, can't land on the moved entry.</summary>
    [Fact]
    public async Task AMoveDuringACheckIn_KeepsTheOldBridgesIssuesOffTheEntry()
    {
        using var key = new BridgeSealedCredentialTests.BridgeKey();
        var (a, b, _) = await ThreeBridgesAsync("race-checkin", key.PublicKey);
        var entryId = await ConnectAsync(a, "bambu", "192.168.1.22", "RACE" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant());
        var userId = await UserIdAsync(factory, a.Subject);
        var version = (await CheckInOkAsync(_client, a.Token, new { })).GetProperty("desired").GetProperty("version").GetString();

        factory.Interceptor.When = WritesIssues;
        factory.Interceptor.Before = async () =>
        {
            using var scope = factory.Services.CreateScope();
            var bridges = scope.ServiceProvider.GetRequiredService<IBridgeService>();
            Assert.NotNull(await bridges.MoveAsync(userId, a.GrantId, entryId, new MoveBridgePrinterDto { ToBridgeId = b.GrantId, Host = "192.168.2.22" }, TestContext.Current.CancellationToken));
        };
        var checkIn = await CheckInOkAsync(_client, a.Token, new
        {
            credentialIssuesVersion = version,
            credentialIssues = new[] { new { entryId, field = "access_code", code = "missing" } },
        });

        Assert.Null(factory.Interceptor.Before);
        Assert.Empty(checkIn.GetProperty("desired").GetProperty("printers").EnumerateArray());
        Assert.Equal(b.GrantId, await BridgeOfEntryAsync(entryId));
        var onB = Assert.Single((await GetAsUserAsync(_client, a.Subject, $"/api/bridges/{b.GrantId}/printers")).EnumerateArray());
        Assert.Empty(onB.GetProperty("credentialIssues").EnumerateArray());
    }
}
