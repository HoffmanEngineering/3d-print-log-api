using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PrintLogApi.Achievements;
using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Achievements.Triggers;
using PrintLogApi.IntegrationTests.Support;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Achievements;

/// <summary>
/// Opt-in wall-clock checks. CI and deploy run an unfiltered <c>dotnet test</c>, and a timing
/// assertion on a shared runner is noise, so these skip unless asked for:
/// <list type="bullet">
/// <item><c>ACHIEVEMENT_PERF=1</c> runs the SQLite benchmark.</item>
/// <item>Adding <c>ACHIEVEMENT_SQLSERVER_CONNECTION</c> (a Docker SQL Server) runs the SQL Server
/// benchmark against a freshly migrated database, which is also the only test that proves the
/// aggregate query is valid T-SQL, plus the retrying-execution-strategy check.</item>
/// </list>
/// A failure here means fix the query shape (inspect it with <c>LogTo</c>), not loosen the limit.
/// </summary>
public class AchievementPerformanceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AchievementPerformanceTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool PerfEnabled => Environment.GetEnvironmentVariable("ACHIEVEMENT_PERF") == "1";

    private static string? SqlServerConnection => Environment.GetEnvironmentVariable("ACHIEVEMENT_SQLSERVER_CONNECTION");

    private static AchievementEvaluator Evaluator(IServiceProvider sp, PrintLogContext db) =>
        new(db, sp.GetServices<IAchievementMetric>(),
            new NotificationService(db, sp.GetRequiredService<AutoMapper.IMapper>(), sp.GetRequiredService<PrintLogApi.Services.Push.IPushDispatchService>()),
            new CatalogVersionProvider(), new AchievementUserLocks(), TimeProvider.System, NullLogger<AchievementEvaluator>.Instance);

    /// <summary>Times the launch catch-up for the large user, after a warm-up pass on a small one.</summary>
    private static async Task<TimeSpan> TimeCatchUpAsync(IServiceProvider sp, Func<PrintLogContext> newContext)
    {
        User large, small;
        await using (var seed = newContext())
        {
            large = await AchievementLargeUserSeed.SeedAsync(seed);
            small = await AchievementTestData.CreateUserAsync(seed);
            await AchievementTestData.AddPrintAsync(seed, small);
        }

        await using (var warm = newContext())
        {
            await Evaluator(sp, warm).EvaluateAsync(small.Id, AchievementTrigger.None, EvaluationMode.Full, Ct);
        }

        await using var db = newContext();
        var stopwatch = Stopwatch.StartNew();
        var result = await Evaluator(sp, db).EvaluateAsync(large.Id, AchievementTrigger.None, EvaluationMode.Full, Ct);
        stopwatch.Stop();
        Assert.Contains(("prints-logged", 6), result.Granted);
        return stopwatch.Elapsed;
    }

    [Fact]
    public async Task FullPass_10kPrints_Sqlite_Under500ms()
    {
        Assert.SkipUnless(PerfEnabled, "Set ACHIEVEMENT_PERF=1 to run timing benchmarks.");
        using var scope = _factory.Services.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<PrintLogContext>().Database.GetDbConnection();

        var elapsed = await TimeCatchUpAsync(scope.ServiceProvider,
            () => new PrintLogContext(new DbContextOptionsBuilder<PrintLogContext>().UseSqlite(connection).Options));

        Assert.True(elapsed < TimeSpan.FromMilliseconds(500), $"Full catch-up pass took {elapsed.TotalMilliseconds:F0} ms");
    }

    private async Task<DbContextOptions<PrintLogContext>> SqlServerOptionsAsync(Action<DbContextOptionsBuilder<PrintLogContext>>? configure = null)
    {
        var builder = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer(SqlServerConnection, sql => sql.EnableRetryOnFailure());
        configure?.Invoke(builder);
        var options = builder.Options;
        await using var db = new PrintLogContext(options);
        await db.Database.MigrateAsync(Ct);
        return options;
    }

    [Fact]
    public async Task FullPass_10kPrints_SqlServer_Under150ms()
    {
        Assert.SkipUnless(PerfEnabled && SqlServerConnection is not null,
            "Set ACHIEVEMENT_PERF=1 and ACHIEVEMENT_SQLSERVER_CONNECTION to run the SQL Server benchmark.");
        using var scope = _factory.Services.CreateScope();
        var options = await SqlServerOptionsAsync();

        var elapsed = await TimeCatchUpAsync(scope.ServiceProvider, () => new PrintLogContext(options));

        Assert.True(elapsed < TimeSpan.FromMilliseconds(150), $"Full catch-up pass took {elapsed.TotalMilliseconds:F0} ms");

        // Rarity's single grouped statement must be valid T-SQL too; SQLite would accept more.
        await using var rarityDb = new PrintLogContext(options);
        var rarity = await AchievementRarityService.ComputeAsync(rarityDb, Ct);
        Assert.Contains(rarity.Entries, e => e.Key == "prints-logged" && e.Tier == 6);
    }

    /// <summary>The real SQL Server strategy, also retrying on the test's marker exception.</summary>
    private sealed class TestSqlServerRetryingStrategy(DbContext context)
        : SqlServerRetryingExecutionStrategy(context, maxRetryCount: 3)
    {
        protected override bool ShouldRetryOn(Exception exception) =>
            exception is TransientTestException || base.ShouldRetryOn(exception);

        protected override TimeSpan? GetNextDelay(Exception lastException) =>
            base.GetNextDelay(lastException) is null ? null : TimeSpan.Zero;
    }

    [Fact]
    public async Task RetriedTransaction_SqlServer_RunsExactlyOnceAfterCommittedAttempt()
    {
        Assert.SkipUnless(PerfEnabled && SqlServerConnection is not null,
            "Set ACHIEVEMENT_PERF=1 and ACHIEVEMENT_SQLSERVER_CONNECTION to run SQL Server checks.");
        var tracker = new AchievementTriggerTracker();
        var recorder = new RecordingPassRunner();
        var options = await SqlServerOptionsAsync(b => b.AddInterceptors(
            new AchievementSaveChangesInterceptor(tracker, recorder, new AchievementPassOptions(), NullLogger<AchievementSaveChangesInterceptor>.Instance),
            new AchievementTransactionInterceptor(tracker, recorder, new AchievementPassOptions(), NullLogger<AchievementTransactionInterceptor>.Instance)));

        await using var db = new PrintLogContext(options);
        var user = await AchievementTestData.CreateUserAsync(db);
        var printer = await AchievementTestData.AddPrinterAsync(db, user);
        recorder.Runs.Clear();

        var attempt = 0;
        await new TestSqlServerRetryingStrategy(db).ExecuteAsync(async () =>
        {
            attempt++;
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            if (attempt == 1)
            {
                db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "Lost", CreatedById = user.Id, UpdatedById = user.Id });
                await db.SaveChangesAsync(Ct);
                throw new TransientTestException();
            }

            db.Prints.Add(new Print { Title = "Kept", PrinterId = printer.Id, CreatedById = user.Id, UpdatedById = user.Id });
            await db.SaveChangesAsync(Ct);
            await tx.CommitAsync(Ct);
        });

        Assert.Equal(2, attempt);
        Assert.Equal([AchievementTrigger.PrintAdded], recorder.For(user.Id));
    }
}
