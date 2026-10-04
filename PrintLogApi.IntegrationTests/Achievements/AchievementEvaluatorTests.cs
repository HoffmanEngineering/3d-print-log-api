using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PrintLogApi.Achievements;
using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Achievements;

public class AchievementEvaluatorTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AchievementEvaluatorTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedVersion(int version) : ICatalogVersionProvider
    {
        public int Version { get; } = version;
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

    /// <summary>An evaluator over its own context on the shared connection, so tests can attach interceptors.</summary>
    private static (AchievementEvaluator Evaluator, PrintLogContext Db) Build(IServiceProvider sp, int? version = null, params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(sp.GetRequiredService<PrintLogContext>().Database.GetDbConnection())
            .AddInterceptors(interceptors)
            .Options;
        var db = new PrintLogContext(options);
        var notifications = new NotificationService(db, sp.GetRequiredService<AutoMapper.IMapper>(),
            sp.GetRequiredService<PrintLogApi.Services.Push.IPushDispatchService>());
        var evaluator = new AchievementEvaluator(db, sp.GetServices<IAchievementMetric>(), notifications,
            new FixedVersion(version ?? AchievementCatalog.Version), TimeProvider.System, NullLogger<AchievementEvaluator>.Instance);
        return (evaluator, db);
    }

    private async Task<(IServiceScope Scope, PrintLogContext Db, User User)> ArrangeAsync(int catalogVersion = AchievementCatalog.Version)
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await AchievementTestData.CreateUserAsync(db);
        user.AchievementCatalogVersion = catalogVersion;
        await db.SaveChangesAsync(Ct);
        return (scope, db, user);
    }

    private static Task<List<(string, int)>> HeldAsync(PrintLogContext db, long userId) =>
        db.UserAchievements.Where(a => a.UserId == userId).OrderBy(a => a.AchievementKey).ThenBy(a => a.Tier)
            .Select(a => ValueTuple.Create(a.AchievementKey, a.Tier)).ToListAsync(Ct);

    private static async Task AddUndatedPrintsAsync(PrintLogContext db, User user, int count)
    {
        // No StartDate, so no date or streak badge can qualify and the expected grants stay exact.
        for (var i = 0; i < count; i++)
        {
            await AchievementTestData.AddPrintAsync(db, user, p => p.StartDate = null);
        }
    }

    [Fact]
    public async Task Triggered_GrantsOnlySelectedFamilies()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        await AddUndatedPrintsAsync(db, user, 1);
        var (evaluator, evalDb) = Build(scope.ServiceProvider);
        await using var __ = evalDb;

        var result = await evaluator.EvaluateAsync(user.Id, AchievementTrigger.PrinterChanged, EvaluationMode.Triggered, Ct);

        Assert.Equal([("first-printer", 1)], result.Granted);
        Assert.Equal([("first-printer", 1)], await HeldAsync(db, user.Id));
    }

    [Fact]
    public async Task Full_GrantsEverythingQualified()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        await AddUndatedPrintsAsync(db, user, 1);
        var (evaluator, evalDb) = Build(scope.ServiceProvider);
        await using var __ = evalDb;

        var result = await evaluator.EvaluateAsync(user.Id, AchievementTrigger.None, EvaluationMode.Full, Ct);

        Assert.Equal([("first-print", 1), ("first-printer", 1)], await HeldAsync(db, user.Id));
        Assert.Equal(2, result.Granted.Count);
        Assert.Equal(1, result.Metrics["prints.count"].Best);
        Assert.Equal(AchievementCatalog.Definitions.Select(d => d.MetricKey).Distinct().Count(), result.Metrics.Count);
        Assert.Equal(2, result.Held.Count);
    }

    [Fact]
    public async Task RunTwice_IsIdempotent()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        await AddUndatedPrintsAsync(db, user, 1);
        var (evaluator, evalDb) = Build(scope.ServiceProvider);
        await using var __ = evalDb;

        await evaluator.EvaluateAsync(user.Id, AchievementTrigger.None, EvaluationMode.Full, Ct);
        var second = await evaluator.EvaluateAsync(user.Id, AchievementTrigger.None, EvaluationMode.Full, Ct);

        Assert.Empty(second.Granted);
        Assert.Equal(2, (await HeldAsync(db, user.Id)).Count);
        Assert.Equal(2, await db.Notifications.CountAsync(n => n.UserId == user.Id, Ct));
    }

    [Fact]
    public async Task Grant_CreatesExactlyOneNotificationEach()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        await AddUndatedPrintsAsync(db, user, 10);
        var (evaluator, evalDb) = Build(scope.ServiceProvider);
        await using var __ = evalDb;

        var result = await evaluator.EvaluateAsync(user.Id, AchievementTrigger.None, EvaluationMode.Full, Ct);

        var metadata = await db.Notifications.Where(n => n.UserId == user.Id).Select(n => n.Metadata).ToListAsync(Ct);
        Assert.Equal(result.Granted.Count, metadata.Count);
        Assert.Contains("""{"v":1,"key":"prints-logged","tier":1}""", metadata);
        Assert.All(await db.Notifications.Where(n => n.UserId == user.Id).ToListAsync(Ct), n => Assert.False(n.IsRead));
    }

    [Fact]
    public async Task LaunchCatchUp_GrantsRetroactive_NotificationsRead_OneSummary()
    {
        var (scope, db, user) = await ArrangeAsync(catalogVersion: 0);
        using var _ = scope;
        await AddUndatedPrintsAsync(db, user, 12);
        // The migration's backfill row.
        db.UserAchievements.Add(new UserAchievement { UserId = user.Id, AchievementKey = "plugged-in", Tier = 1, UnlockedAt = DateTime.UtcNow, Retroactive = true });
        await db.SaveChangesAsync(Ct);
        var (evaluator, evalDb) = Build(scope.ServiceProvider);
        await using var __ = evalDb;

        // A triggered pass still runs the full catch-up when the user is behind the catalog.
        await evaluator.EvaluateAsync(user.Id, AchievementTrigger.PrintAdded, EvaluationMode.Triggered, Ct);

        var rows = await db.UserAchievements.AsNoTracking().Where(a => a.UserId == user.Id).ToListAsync(Ct);
        Assert.Equal(
            [("first-print", 1), ("first-printer", 1), ("plugged-in", 1), ("prints-logged", 1)],
            rows.OrderBy(r => r.AchievementKey).Select(r => (r.AchievementKey, r.Tier)));
        Assert.All(rows, r => Assert.True(r.Retroactive));

        var notes = await db.Notifications.AsNoTracking().Where(n => n.UserId == user.Id).ToListAsync(Ct);
        var summary = Assert.Single(notes, n => !n.IsRead);
        // Every retroactive row the user holds, the migration's plugged-in included. The printer
        // every print needs is what earns first-printer.
        Assert.Equal("""{"v":1,"summary":true,"count":4}""", summary.Metadata);
        Assert.Equal(3, notes.Count(n => n.IsRead));

        var version = await db.Users.AsNoTracking().Where(u => u.Id == user.Id).Select(u => u.AchievementCatalogVersion).SingleAsync(Ct);
        Assert.Equal(AchievementCatalog.Version, version);
    }

    [Fact]
    public async Task LaterBump_GrantsAreNotRetroactive()
    {
        var (scope, db, user) = await ArrangeAsync(catalogVersion: 1);
        using var _ = scope;
        await AddUndatedPrintsAsync(db, user, 1);
        var (evaluator, evalDb) = Build(scope.ServiceProvider, version: 2);
        await using var __ = evalDb;

        await evaluator.EvaluateAsync(user.Id, AchievementTrigger.None, EvaluationMode.Triggered, Ct);

        var rows = await db.UserAchievements.AsNoTracking().Where(a => a.UserId == user.Id).ToListAsync(Ct);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(r.Retroactive));
        var notes = await db.Notifications.AsNoTracking().Where(n => n.UserId == user.Id).ToListAsync(Ct);
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.False(n.IsRead));
        Assert.DoesNotContain(notes, n => n.Metadata!.Contains("summary"));
        Assert.Equal(2, await db.Users.AsNoTracking().Where(u => u.Id == user.Id).Select(u => u.AchievementCatalogVersion).SingleAsync(Ct));
    }

    [Fact]
    public async Task CatchUp_WithNothingToGrant_StillAdvancesVersion()
    {
        var (scope, db, user) = await ArrangeAsync(catalogVersion: 0);
        using var _ = scope;
        var (evaluator, evalDb) = Build(scope.ServiceProvider);
        await using var __ = evalDb;

        var result = await evaluator.EvaluateAsync(user.Id, AchievementTrigger.None, EvaluationMode.Triggered, Ct);

        Assert.Empty(result.Granted);
        Assert.Equal(0, await db.Notifications.CountAsync(n => n.UserId == user.Id, Ct));
        Assert.Equal(AchievementCatalog.Version,
            await db.Users.AsNoTracking().Where(u => u.Id == user.Id).Select(u => u.AchievementCatalogVersion).SingleAsync(Ct));
    }

    [Fact]
    public async Task Conflict_RetriesAndKeepsNonConflictingGrants()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        await AddUndatedPrintsAsync(db, user, 10);
        var (evaluator, evalDb) = Build(scope.ServiceProvider);
        await using var __ = evalDb;

        // A concurrent pass wins first-print after this one has loaded its held tiers.
        var raced = false;
        evaluator.BeforePersist = async () =>
        {
            if (raced)
            {
                return;
            }
            raced = true;
            db.UserAchievements.Add(new UserAchievement { UserId = user.Id, AchievementKey = "first-print", Tier = 1, UnlockedAt = DateTime.UtcNow });
            db.Notifications.Add(AchievementNotificationFactory.ForGrant(user.Id, AchievementCatalog.Find("first-print")!, 1, false));
            await db.SaveChangesAsync(Ct);
        };

        var result = await evaluator.EvaluateAsync(user.Id, AchievementTrigger.None, EvaluationMode.Full, Ct);

        Assert.Contains(("prints-logged", 1), result.Granted);
        Assert.DoesNotContain(("first-print", 1), result.Granted);
        Assert.Equal(1, await db.UserAchievements.CountAsync(a => a.UserId == user.Id && a.AchievementKey == "first-print", Ct));
        var firstPrintNotes = await db.Notifications.CountAsync(n => n.UserId == user.Id && n.Metadata!.Contains("\"first-print\""), Ct);
        Assert.Equal(1, firstPrintNotes);
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == user.Id && n.Metadata!.Contains("\"prints-logged\""), Ct));
    }

    [Fact]
    public async Task NoGrants_NoSave()
    {
        var (scope, _, user) = await ArrangeAsync();
        using var __ = scope;
        var counter = new SaveCounter();
        var (evaluator, evalDb) = Build(scope.ServiceProvider, null, counter);
        await using var ___ = evalDb;

        var result = await evaluator.EvaluateAsync(user.Id, AchievementTrigger.None, EvaluationMode.Full, Ct);

        Assert.Empty(result.Granted);
        Assert.Equal(0, counter.Saves);
    }

    [Fact]
    public async Task UnknownUser_ReturnsEmpty()
    {
        using var scope = _factory.Services.CreateScope();
        var (evaluator, evalDb) = Build(scope.ServiceProvider);
        await using var _ = evalDb;

        var result = await evaluator.EvaluateAsync(long.MaxValue, AchievementTrigger.None, EvaluationMode.Full, Ct);

        Assert.Empty(result.Granted);
        Assert.Empty(result.Held);
    }
}
