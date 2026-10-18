using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Caching;
using PrintLogApi.Models;
using PrintLogApi.Services;
using PrintLogApi.Services.Sync;
using Xunit;

namespace PrintLogApi.IntegrationTests.Sync;

/// <summary>
/// How the snapshot is cached and validated (#234 review): a 304 must not rebuild the core, the
/// tag must follow the content so a missed version bump recovers, and time zone aliases share one
/// canonical zone.
/// </summary>
public class SyncSnapshotCachingTests(SyncTestFactory factory) : IClassFixture<SyncTestFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HybridCacheEntryOptions Peek = new()
    {
        Flags = HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite,
    };

    private HybridCache Cache => factory.Services.GetRequiredService<HybridCache>();

    private string Guid(User user) => factory.Services.GetRequiredService<ICacheVersionService>().GetUserCacheVersion(user.Id);

    private async Task<Printer> AddPrinterAsync(User user, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var printer = new Printer { Name = name, UserId = user.Id, IsActive = true, CategoryNickname = "FDM" };
        db.Printers.Add(printer);
        await db.SaveChangesAsync(Ct);
        return printer;
    }

    private async Task RenameBehindTheApiAsync(Printer printer, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        (await db.Printers.FindAsync([printer.Id], Ct))!.Name = name;
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

    [Fact]
    public async Task ConditionalRequest_AfterTheCoreExpired_Returns304WithoutRebuildingIt()
    {
        var user = await factory.CreateUserAsync();
        await AddPrinterAsync(user, "Voron");
        var first = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot"), Ct);
        var guid = Guid(user);
        await Cache.RemoveAsync(SyncSnapshotService.CoreKey(user.Id, guid), Ct);

        factory.Commands.Clear();
        var response = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot", first.Headers.ETag!.ToString()), Ct);
        // TestAuthHandler looks the user up on every request (the real ClaimsTransformer caches it).
        var statements = factory.Commands.Recorded.Where(s => !s.Contains("FROM \"Users\"", StringComparison.Ordinal)).ToList();

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        // The core factory never ran: nothing was cached back under its key.
        var core = await Cache.GetOrCreateAsync<SyncSnapshotService.CoreEntry?>(
            SyncSnapshotService.CoreKey(user.Id, guid), static _ => ValueTask.FromResult<SyncSnapshotService.CoreEntry?>(null), Peek, cancellationToken: Ct);
        Assert.Null(core);
        // And the only SQL was the connection overlay.
        var statement = Assert.Single(statements);
        Assert.Contains("Connections", statement);
    }

    [Fact]
    public async Task ETag_FollowsTheContent_SoAChangeWithoutAVersionBumpRecoversOnceTheEntriesExpire()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Before");
        var first = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot"), Ct);
        var etag = first.Headers.ETag!.ToString();
        var guid = Guid(user);

        // Same content after expiry: same tag, still a 304.
        await Cache.RemoveAsync(SyncSnapshotService.CoreKey(user.Id, guid), Ct);
        await Cache.RemoveAsync(SyncSnapshotService.HashKey(user.Id, guid), Ct);
        var unchanged = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot", etag), Ct);

        // A write that forgot to bump the version, then expiry: the new content gets a new tag.
        await RenameBehindTheApiAsync(printer, "After");
        await Cache.RemoveAsync(SyncSnapshotService.CoreKey(user.Id, guid), Ct);
        await Cache.RemoveAsync(SyncSnapshotService.HashKey(user.Id, guid), Ct);
        var changed = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot", etag), Ct);

        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(etag, changed.Headers.ETag!.ToString());
        Assert.Equal("After", (await BodyAsync(changed)).GetProperty("printers")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("US/Eastern")]
    [InlineData("Eastern Standard Time")]
    [InlineData("America/New_York")]
    public async Task TimeZoneAliases_ShareOneCanonicalZone(string alias)
    {
        var user = await factory.CreateUserAsync();
        var canonical = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot?timeZone=America/New_York"), Ct);

        var response = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot?timeZone=" + Uri.EscapeDataString(alias)), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("America/New_York", (await BodyAsync(response)).GetProperty("account").GetProperty("timeZone").GetString());
        Assert.Equal(canonical.Headers.ETag!.ToString(), response.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task TimeZone_UtcAliases_AreReportedAsUtc()
    {
        var user = await factory.CreateUserAsync();

        var body = await BodyAsync(await _client.SendAsync(factory.Get(user, "/api/sync/snapshot?timeZone=Etc/UTC"), Ct));

        Assert.Equal("UTC", body.GetProperty("account").GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task TimeZoneOmitted_UsesTheSavedTimeZoneSetting()
    {
        var user = await factory.CreateUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.UserSettings.Add(new UserSetting
            {
                UserId = user.Id,
                UserSettingTypeId = EvaluationContext.TimeZoneSettingTypeId,
                Value = "Europe/Berlin",
                CreatedById = user.Id,
                UpdatedById = user.Id,
            });
            await db.SaveChangesAsync(Ct);
        }

        var body = await BodyAsync(await _client.SendAsync(factory.Get(user, "/api/sync/snapshot"), Ct));

        Assert.Equal("Europe/Berlin", body.GetProperty("account").GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task TimeZoneOmitted_WithoutASavedSetting_IsUtc()
    {
        var user = await factory.CreateUserAsync();

        var body = await BodyAsync(await _client.SendAsync(factory.Get(user, "/api/sync/snapshot"), Ct));

        Assert.Equal("UTC", body.GetProperty("account").GetProperty("timeZone").GetString());
    }

    /// <summary>
    /// A heavy but realistic account: 300 spools (most with two colors and a storage location),
    /// six printers with loaded slots. Its cached core is what the shared 8 MiB budget is charged,
    /// so the figure is noted in CacheBudget and AGENTS.md.
    /// </summary>
    [Fact]
    public async Task RealisticCore_StaysWellInsideTheBudget()
    {
        var user = await factory.CreateUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var printers = Enumerable.Range(0, 6).Select(i => new Printer { Name = $"Printer {i}", Make = "Bambu Lab", Model = "X1 Carbon", UserId = user.Id, IsActive = true, CategoryNickname = "FDM", SlotCount = 4 }).ToList();
            db.Printers.AddRange(printers);
            var spools = Enumerable.Range(1, 300).Select(i => new Filament
            {
                Id = System.Guid.NewGuid(),
                DisplayName = $"Prusament PLA Galaxy Black {i}",
                Brand = "Prusament",
                MaterialType = "PLA",
                MaterialCategoryNickname = "filament",
                MaterialDensityGramPerCubicCm = 1.24,
                DiameterMm = 1.75,
                InitialNominalWeightMg = 1_000_000,
                SpoolNumber = i,
                ColorName = "Galaxy Black",
                Colors = ["#1A1A1A", "#C0C0C0"],
                StorageLocation = $"Dry box {i % 12}",
                IsActive = i % 5 != 0,
                CreatedById = user.Id,
                UpdatedById = user.Id,
            }).ToList();
            db.Filaments.AddRange(spools);
            await db.SaveChangesAsync(Ct);
            foreach (var (printer, p) in printers.Select((p, i) => (p, i)))
            {
                for (var slot = 0; slot < 4; slot++)
                {
                    db.PrinterFilament.Add(new PrinterFilament { PrinterId = printer.Id, FilamentId = spools[p * 4 + slot].Id, Slot = slot, SlotLabel = $"AMS A{slot + 1}", LoadedDateTime = SyncTestFactory.Start });
                }
            }

            await db.SaveChangesAsync(Ct);
        }

        CoreSize size;
        using (var scope = factory.Services.CreateScope())
        {
            var entry = await SyncSnapshotService.LoadCoreAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), user.Id, null, Ct);
            size = new CoreSize(JsonSerializer.SerializeToUtf8Bytes(entry).Length);
        }

        // Within a factor of 1.5 of the recorded figure either way, so CacheBudget's note stays true
        // and a regression that would crowd the shared budget fails here.
        Assert.InRange(size.Bytes, CacheBudget.SyncSnapshotCoreBytes * 2 / 3, CacheBudget.SyncSnapshotCoreBytes * 3 / 2);
    }

    private sealed record CoreSize(int Bytes);

    [Fact]
    public async Task NumberingUnnumberedSpools_BumpsTheVersion_OnlyWhenItNumbersOne()
    {
        var user = await factory.CreateUserAsync();
        var versions = factory.Services.GetRequiredService<ICacheVersionService>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.Filaments.Add(new Filament
            {
                Id = System.Guid.NewGuid(),
                DisplayName = "Legacy",
                MaterialCategoryNickname = "filament",
                MaterialDensityGramPerCubicCm = 1.24,
                IsActive = true,
                CreatedById = user.Id,
                UpdatedById = user.Id,
            });
            await db.SaveChangesAsync(Ct);
        }

        var before = versions.GetUserCacheVersion(user.Id);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IFilamentService>().NumberUnnumberedSpoolsAsync(user.Id, Ct);
        }

        var afterNumbering = versions.GetUserCacheVersion(user.Id);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IFilamentService>().NumberUnnumberedSpoolsAsync(user.Id, Ct);
        }

        Assert.NotEqual(before, afterNumbering);
        Assert.Equal(afterNumbering, versions.GetUserCacheVersion(user.Id));
    }
}
