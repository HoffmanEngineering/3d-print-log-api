using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Achievements;
using PrintLogApi.Achievements.Triggers;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Achievements;
using Xunit;

namespace PrintLogApi.IntegrationTests.Achievements;

public class AchievementsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AchievementsControllerTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] HiddenKeys = ["spaghetti", "comeback", "night-owl", "new-year"];

    /// <summary>A scope whose context is suppressed, so seeding never grants on its own.</summary>
    private (IServiceScope Scope, PrintLogContext Db) QuietScope()
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        scope.ServiceProvider.GetRequiredService<AchievementTriggerTracker>().Suppress(db);
        return (scope, db);
    }

    private async Task<User> CaughtUpUserAsync(PrintLogContext db)
    {
        var user = await AchievementTestData.CreateUserAsync(db);
        user.AchievementCatalogVersion = AchievementCatalog.Version;
        await db.SaveChangesAsync(Ct);
        return user;
    }

    private static async Task HoldAsync(PrintLogContext db, User user, string key, int tier = 1)
    {
        db.UserAchievements.Add(new UserAchievement { UserId = user.Id, AchievementKey = key, Tier = tier, UnlockedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(Ct);
    }

    private HttpRequestMessage As(User user, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        return request;
    }

    private async Task<MyAchievementsDto> MeAsync(User user, HttpClient? client = null)
    {
        var response = await (client ?? _factory.CreateClient()).SendAsync(As(user, HttpMethod.Get, "/api/achievements/me"), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MyAchievementsDto>(Ct))!;
    }

    [Fact]
    public async Task Catalog_Anonymous_OmitsHiddenFamiliesAndKeys()
    {
        var response = await _factory.CreateClient().GetAsync("/api/achievements/catalog", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.All(HiddenKeys, key => Assert.DoesNotContain(key, body));
        var catalog = (await response.Content.ReadFromJsonAsync<AchievementCatalogDto>(Ct))!;
        Assert.Equal(AchievementCatalog.Version, catalog.Version);
        Assert.Equal(30, catalog.Families.Count);
        Assert.Equal(4, catalog.HiddenCount);
        var hours = catalog.Families.Single(f => f.Key == "print-hours");
        Assert.Equal("Rack up 1,000 hours of total print time.", hours.Tiers[4].Description);
        Assert.Equal(1000, hours.Tiers[4].Threshold);
    }

    [Fact]
    public async Task Catalog_HasCacheControlHeader()
    {
        var response = await _factory.CreateClient().GetAsync("/api/achievements/catalog", Ct);

        Assert.True(response.Headers.CacheControl!.Public);
        Assert.Equal(TimeSpan.FromHours(1), response.Headers.CacheControl.MaxAge);
    }

    [Fact]
    public async Task Me_RequiresAuthentication()
    {
        var response = await _factory.CreateClient().GetAsync("/api/achievements/me", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_ReconcilesMissedGrant()
    {
        var (scope, db) = QuietScope();
        using var _ = scope;
        var user = await CaughtUpUserAsync(db);
        await AchievementTestData.AddPrintAsync(db, user);
        Assert.False(await db.UserAchievements.AnyAsync(a => a.UserId == user.Id, Ct));

        var me = await MeAsync(user);

        Assert.Contains(me.Families, f => f.Key == "first-print" && f.Tiers.Any(t => t.Tier == 1));
        Assert.True(await db.UserAchievements.AnyAsync(a => a.UserId == user.Id && a.AchievementKey == "first-print", Ct));
        Assert.Equal(AchievementCatalog.TotalTierCount, me.TotalTierCount);
        Assert.Equal(me.Families.Sum(f => f.Tiers.Count), me.EarnedTierCount);
    }

    [Fact]
    public async Task Me_ProgressBestNeverBelowEarnedThreshold()
    {
        var (scope, db) = QuietScope();
        using var _ = scope;
        var user = await CaughtUpUserAsync(db);
        // Earned a 3-day streak whose prints have since been deleted.
        await HoldAsync(db, user, "daily-streak", 1);

        var streak = (await MeAsync(user)).Families.Single(f => f.Key == "daily-streak");

        Assert.NotNull(streak.Progress);
        Assert.True(streak.Progress!.Best >= 3);
        Assert.Equal(0, streak.Progress.Current);
        Assert.Equal(5, streak.Progress.NextThreshold);
    }

    [Fact]
    public async Task Me_ProgressIsNullWhenMaxed()
    {
        var (scope, db) = QuietScope();
        using var _ = scope;
        var user = await CaughtUpUserAsync(db);
        await AchievementTestData.AddPrintAsync(db, user);

        var me = await MeAsync(user);

        Assert.Null(me.Families.Single(f => f.Key == "first-print").Progress);
        Assert.Equal(new AchievementProgressDto(1, 1, 10), me.Families.Single(f => f.Key == "prints-logged").Progress);
    }

    [Fact]
    public async Task Me_RevealsEarnedHiddenFamilyOnly()
    {
        var (scope, db) = QuietScope();
        using var _ = scope;
        var user = await CaughtUpUserAsync(db);
        await AchievementTestData.AddPrintAsync(db, user, p => { p.Status = Print.PrintStatus.Failed; p.StartDate = null; });

        var me = await MeAsync(user);

        var revealed = Assert.Single(me.RevealedHidden);
        Assert.Equal("spaghetti", revealed.Key);
        Assert.Equal("Spaghetti Monster", revealed.Title);
        Assert.Contains(me.Families, f => f.Key == "spaghetti");
        Assert.DoesNotContain(me.Families, f => f.Key is "comeback" or "night-owl" or "new-year");
    }

    [Fact]
    public async Task Me_NextHint_FollowsHintOrder()
    {
        var (scope, db) = QuietScope();
        using var _ = scope;
        var user = await CaughtUpUserAsync(db);

        Assert.Equal(new NextHintDto("first-printer", 1, "/printers/new"), (await MeAsync(user)).NextHint);

        await AchievementTestData.AddPrinterAsync(db, user);
        Assert.Equal(new NextHintDto("first-material", 1, "/materials/new"), (await MeAsync(user)).NextHint);
    }

    [Fact]
    public async Task Me_NextHint_NullWhenDismissed()
    {
        var (scope, db) = QuietScope();
        using var _ = scope;
        var user = await CaughtUpUserAsync(db);
        var client = _factory.CreateClient();

        var dismiss = As(user, HttpMethod.Post, "/api/achievements/hint/dismiss");
        dismiss.Content = JsonContent.Create(new { key = "first-printer", tier = 1 });
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(dismiss, Ct)).StatusCode);

        Assert.Null((await MeAsync(user, client)).NextHint);
    }

    [Fact]
    public async Task Me_NextHint_ProgressFallback_SkipsInactiveStreaks()
    {
        var (scope, db) = QuietScope();
        using var _ = scope;
        var user = await CaughtUpUserAsync(db);
        foreach (var def in AchievementCatalog.Definitions.Where(d => d.Category == AchievementCategory.GettingStarted))
        {
            await HoldAsync(db, user, def.Key);
        }
        // A two-day streak 100 days ago: 2/3 of the way to "On a Roll", but no longer running.
        var start = DateTimeOffset.UtcNow.AddDays(-100);
        foreach (var day in new[] { 0, 1 })
        {
            var print = await AchievementTestData.AddPrintAsync(db, user, p => p.StartDate = start.AddDays(day));
            await AchievementTestData.SetCreatedDateAsync(db, print.Id, start.AddDays(day).AddMinutes(5).UtcDateTime);
        }

        // One printer of the two "Print Farm" needs (1/2) beats every other active family.
        Assert.Equal(new NextHintDto("printers-owned", 1, "/achievements"), (await MeAsync(user)).NextHint);
    }

    [Fact]
    public async Task DismissHint_StoresKeyTier()
    {
        var (scope, db) = QuietScope();
        using var _ = scope;
        var user = await CaughtUpUserAsync(db);
        var client = _factory.CreateClient();

        foreach (var (key, tier) in new[] { ("first-printer", 1), ("prints-logged", 2) })
        {
            var request = As(user, HttpMethod.Post, "/api/achievements/hint/dismiss");
            request.Content = JsonContent.Create(new { key, tier });
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(request, Ct)).StatusCode);
        }

        var values = await db.UserSettings.AsNoTracking()
            .Where(s => s.UserId == user.Id && s.UserSettingTypeId == 19).Select(s => s.Value).ToListAsync(Ct);
        Assert.Equal(["prints-logged:2"], values);
    }

    [Fact]
    public async Task DismissHint_RejectsUnknownKey()
    {
        var (scope, db) = QuietScope();
        using var _ = scope;
        var user = await CaughtUpUserAsync(db);

        var request = As(user, HttpMethod.Post, "/api/achievements/hint/dismiss");
        request.Content = JsonContent.Create(new { key = "not-a-badge", tier = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.CreateClient().SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task Rarity_DenominatorIsEvaluatedUsersOnly()
    {
        var (scope, db) = QuietScope();
        using var _ = scope;
        var evaluated = await CaughtUpUserAsync(db);
        var neverEvaluated = await AchievementTestData.CreateUserAsync(db);
        // A key no real grant uses, so the expected holder count is exact.
        await HoldAsync(db, evaluated, "rarity-probe");
        await HoldAsync(db, neverEvaluated, "rarity-probe");

        var (countingDb, counter) = AchievementMetricTestKit.CountingContext(scope.ServiceProvider);
        await using var ___ = countingDb;
        var table = await AchievementRarityService.ComputeAsync(countingDb, Ct);

        // One statement: holders and the evaluated-user count read in a single snapshot, so a
        // user finishing catch-up between two reads can't push a tier past 100%.
        Assert.Single(counter.Commands);
        Assert.All(table.Entries, e => Assert.InRange(e.Percent, 0, 100));
        var denominator = await db.Users.CountAsync(u => u.AchievementCatalogVersion > 0, Ct);
        Assert.Equal(100.0 / denominator, table.PercentFor("rarity-probe", 1), 6);
        Assert.Equal(0, table.PercentFor("rarity-probe", 2));
    }

    [Fact]
    public async Task Me_EvaluatorContextIsSuppressed()
    {
        var spy = new CapturingEvaluator();
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddScoped<IAchievementEvaluator>(sp =>
        {
            spy.Captured.Enqueue(sp.GetRequiredService<PrintLogContext>());
            return spy;
        })));
        var tracker = factory.Services.GetRequiredService<AchievementTriggerTracker>();
        User user;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            tracker.Suppress(db);
            user = await CaughtUpUserAsync(db);
        }

        await MeAsync(user, factory.CreateClient());

        var captured = Assert.Single(spy.Captured);
        Assert.True(tracker.IsSuppressed(captured));
    }

    private sealed class CapturingEvaluator : IAchievementEvaluator
    {
        public ConcurrentQueue<PrintLogContext> Captured { get; } = new();

        public Task<EvaluationResult> EvaluateAsync(long userId, AchievementTrigger triggers, EvaluationMode mode, CancellationToken ct = default) =>
            Task.FromResult(EvaluationResult.Empty);
    }
}
