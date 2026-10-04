using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PrintLogApi.Achievements;
using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Achievements.Triggers;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Achievements;

/// <summary>
/// The evaluator's query count must not grow with a user's history: a full pass over 10,000 prints
/// issues the same handful of commands as one over ten. Deterministic, so it runs in every build;
/// wall-clock checks live in the opt-in <see cref="AchievementPerformanceTests"/>.
/// </summary>
public class AchievementQueryShapeTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AchievementQueryShapeTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FullPass_10kPrints_QueryCountIsBounded()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var seedDb = sp.GetRequiredService<PrintLogContext>();
        sp.GetRequiredService<AchievementTriggerTracker>().Suppress(seedDb);
        var user = await AchievementLargeUserSeed.SeedAsync(seedDb);

        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(seedDb.Database.GetDbConnection())
            .AddInterceptors(counter)
            .Options;
        await using var db = new PrintLogContext(options);
        var evaluator = new AchievementEvaluator(db, sp.GetServices<IAchievementMetric>(),
            new NotificationService(db, sp.GetRequiredService<AutoMapper.IMapper>(), sp.GetRequiredService<PrintLogApi.Services.Push.IPushDispatchService>()),
            new CatalogVersionProvider(), new AchievementUserLocks(), TimeProvider.System, NullLogger<AchievementEvaluator>.Instance);

        // The first pass grants (and so writes); the second measures the same history and only reads.
        var first = await evaluator.EvaluateAsync(user.Id, AchievementTrigger.None, EvaluationMode.Full, Ct);
        Assert.Contains(("prints-logged", 6), first.Granted);
        counter.Commands.Clear();

        await evaluator.EvaluateAsync(user.Id, AchievementTrigger.None, EvaluationMode.Full, Ct);

        // User, time zone, print rows, usage rows, photos, six other entities, held tiers.
        Assert.InRange(counter.Commands.Count, 1, 12);
        var printCommands = counter.Commands.Count(c => c.Contains("\"Prints\"", StringComparison.Ordinal));
        Assert.InRange(printCommands, 1, 3);
    }
}
