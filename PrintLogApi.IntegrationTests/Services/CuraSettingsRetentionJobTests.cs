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

    private CuraSettingsRetentionJob Job(int batchSize = 200) => new(
        factory.Services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new CuraSettingsRetentionOptions { BatchSize = batchSize, BatchPause = TimeSpan.Zero }),
        TimeProvider.System,
        NullLogger<CuraSettingsRetentionJob>.Instance);

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

        var deleted = await Job(batchSize: 2).RunOnceAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(5, deleted);
        Assert.Empty(await Remaining("batch:"));
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
