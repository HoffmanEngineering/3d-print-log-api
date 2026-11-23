using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintLogApi.Models;
using PrintLogApi.Services.SlicerSettings;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The slicer uploaders' settings records are deleted after the retention window; connector
/// records (linked to a print, #164) never are. Each test tags its rows through
/// <see cref="CuraSetting.CuraVersion"/> and asserts on its own tag only.
/// </summary>
public class CuraSettingsRetentionJobTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private CuraSettingsRetentionJob Job(int batchSize = 200, TimeSpan? maxRunDuration = null, IServiceScopeFactory? scopeFactory = null) => new(
        scopeFactory ?? factory.Services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new CuraSettingsRetentionOptions
        {
            BatchSize = batchSize,
            BatchPause = TimeSpan.Zero,
            MaxRunDuration = maxRunDuration ?? TimeSpan.FromHours(1),
        }),
        TimeProvider.System,
        NullLogger<CuraSettingsRetentionJob>.Instance);

    /// <summary>Fails every batch the way a SQL timeout or a dropped connection would.</summary>
    private sealed class FailingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("Execution Timeout Expired.");
    }

    private static CuraSetting Record(string tag, DateTimeOffset createdDate, long? printId = null) => new()
    {
        CuraVersion = tag,
        Slicer = "cura",
        _Settings = """{"snapshot":"iVBORw0KGgo="}""",
        CreatedDate = createdDate,
        PrintId = printId,
    };

    private async Task Seed(params CuraSetting[] records)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        context.CuraSettings.AddRange(records);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string[]> Remaining(string tag)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>().CuraSettings
            .Where(c => c.CuraVersion != null && c.CuraVersion.StartsWith(tag))
            .OrderBy(c => c.CuraVersion)
            .Select(c => c.CuraVersion!)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunOnce_DeletesUnlinkedRecordsPastSevenDays_AndKeepsNewerAndLinkedOnes()
    {
        // Starting the host runs the seeder. Read TestPrintId only after that: when this is the
        // first test to touch the factory it is still 0, and the linked record fails its key.
        _ = factory.Services;
        await Seed(
            Record("keep-a:old", Now.AddDays(-8)),
            Record("keep-a:edge", Now.AddDays(-7).AddMinutes(1)),
            Record("keep-a:new", Now.AddHours(-1)),
            Record("keep-a:linked", Now.AddDays(-30), IntegrationTestSeeder.TestPrintId));

        await Job().RunOnceAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(["keep-a:edge", "keep-a:linked", "keep-a:new"], await Remaining("keep-a:"));
    }

    [Fact]
    public async Task RunOnce_DeletesInBatches_UntilNoExpiredRecordIsLeft()
    {
        await Seed(Enumerable.Range(0, 5).Select(i => Record($"batch:{i}", Now.AddDays(-10 - i))).ToArray());

        var result = await Job(batchSize: 2).RunOnceAsync(Now, TestContext.Current.CancellationToken);

        // At least this test's five: a sibling may leave expired records of its own behind (the
        // max-duration test does on purpose), and this run clears those too.
        Assert.True(result.Deleted >= 5, $"deleted {result.Deleted}");
        Assert.False(result.BacklogRemains);
        Assert.Empty(await Remaining("batch:"));
    }

    [Fact]
    public async Task RunOnce_StopsAtMaxRunDuration_AndReportsTheBacklog()
    {
        // Far older than any sibling's records, so this test's are the oldest in the shared database
        // whichever tests ran before it.
        await Seed(Enumerable.Range(0, 5).Select(i => Record($"capped:{i}", Now.AddDays(-1000 - i))).ToArray());

        // A zero cap is reached after the first batch, which is always deleted.
        var result = await Job(batchSize: 2, maxRunDuration: TimeSpan.Zero)
            .RunOnceAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Deleted);
        Assert.True(result.BacklogRemains);
        // Oldest first: the two oldest (-1004 and -1003 days) went.
        Assert.Equal(["capped:0", "capped:1", "capped:2"], await Remaining("capped:"));
    }

    [Fact]
    public async Task RunOnce_WhenABatchFails_LogsInsteadOfThrowing_AndReportsTheBacklog()
    {
        var result = await Job(scopeFactory: new FailingScopeFactory())
            .RunOnceAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Deleted);
        Assert.True(result.BacklogRemains);
    }

    [Fact]
    public void NextRunDelay_IsTheBacklogRetryWhileRecordsAreLeft_ElseTheDailyInterval()
    {
        var settings = new CuraSettingsRetentionOptions();

        Assert.Equal(TimeSpan.FromHours(1),
            CuraSettingsRetentionJob.NextRunDelay(new RetentionRunResult(50, BacklogRemains: true), settings));
        Assert.Equal(TimeSpan.FromDays(1),
            CuraSettingsRetentionJob.NextRunDelay(new RetentionRunResult(3, BacklogRemains: false), settings));
    }

    [Fact]
    public async Task RunOnce_WithNothingExpired_DeletesNothing()
    {
        await Seed(Record("fresh:1", Now.AddDays(-1)));

        await Job().RunOnceAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(["fresh:1"], await Remaining("fresh:"));
    }
}

/// <summary>
/// The suite runs on SQLite, which cannot show what SQL Server is sent. This compiles one batch
/// through the SQL Server provider and captures the DELETE without a server (the connection open
/// and the command are both suppressed): it must filter on the literal <c>[PrintId] IS NULL</c>,
/// which is what lets the optimizer use the filtered index instead of scanning the table, and it
/// must stay bounded by TOP and ordered oldest first.
/// </summary>
public class CuraSettingsRetentionSqlServerTranslationTests
{
    private sealed class CaptureWithoutServer : DbCommandInterceptor, IDbConnectionInterceptor
    {
        public string? Sql { get; private set; }

        public ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Sql = command.CommandText;
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
        }
    }

    [Fact]
    public async Task Batch_IsABoundedOldestFirstDelete_OfUnlinkedRecordsOnly()
    {
        var capture = new CaptureWithoutServer();
        await using var context = new PrintLogContext(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
            .AddInterceptors(capture)
            .Options);

        await CuraSettingsRetentionJob.Expired(context, DateTimeOffset.UtcNow, 200)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        var sql = capture.Sql;
        Assert.NotNull(sql);
        Assert.StartsWith("DELETE", sql.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("[PrintId] IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("[CreatedDate] <", sql, StringComparison.Ordinal);
        Assert.Contains("TOP(", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
    }
}
