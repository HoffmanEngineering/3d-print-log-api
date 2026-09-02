using System.Data.Common;
using AutoMapper;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// Guards the SHAPE of the SQL the grouped feed issues for its standalone-print branch.
///
/// The behavioural tests in <c>PrintsControllerTests</c> cover what the feed returns, and they
/// pass against both the fast and the slow query — which is exactly the problem. Two costly
/// constructs were removed here, and nothing about the results can tell you whether they came
/// back:
///
/// <list type="number">
/// <item>The standalone branch is filtered to <c>ProjectId IS NULL</c>, so the LEFT JOIN that
/// <c>Project.Name.Contains(text)</c> generates can only ever produce NULL and that term can
/// never be true. It cost a join plus a third LIKE — one of them over <c>Notes</c>, an
/// <c>nvarchar(max)</c> column that <c>IX_Prints_Summary</c> does not cover.</item>
/// <item>The filament-totals query used to join <c>PrintFilament</c> back to the filtered print
/// query, re-running the whole text search to arrive at ids the list query had already
/// materialized.</item>
/// </list>
///
/// Both are pure performance wins with no visible result change, which is precisely the kind of
/// edit that gets undone by a well-meaning "why are there two predicates?" refactor. These
/// assertions read the executed SQL so that undoing either one fails loudly.
///
/// The assertions are on structure, not on an exact string: they run against SQLite, and the
/// production provider is SQL Server. What both emit alike is which tables a statement reads and
/// how many statements carry the text-search predicate.
/// </summary>
public class GroupedFeedQueryShapeTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public GroupedFeedQueryShapeTests(CustomWebApplicationFactory factory) => _factory = factory;

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// A PrintService over the SAME seeded connection the factory uses, with a recording
    /// interceptor on its context. Built by hand for the same reason
    /// <c>AnalyticsRoundTripTests</c> does it: the interceptor has to be attached without
    /// changing how the application registers its context.
    /// </summary>
    private (PrintService Service, CommandRecorder Recorder) RecordingService()
    {
        var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var connection = sp.GetRequiredService<PrintLogContext>().Database.GetDbConnection();

        var recorder = new CommandRecorder();
        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(connection)
            .AddInterceptors(recorder)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        var service = new PrintService(
            new PrintLogContext(options),
            sp.GetRequiredService<IMapper>(),
            sp.GetRequiredService<TelemetryClient>(),
            sp.GetRequiredService<IFilamentService>(),
            sp.GetRequiredService<IPrinterService>(),
            sp.GetRequiredService<INotificationService>(),
            sp.GetRequiredService<ICacheVersionService>());

        return (service, recorder);
    }

    /// <summary>
    /// Strips identifier delimiters so one matcher works on either provider. SQLite quotes with
    /// double quotes and SQL Server with brackets, so a matcher written for one silently matches
    /// nothing on the other — a test that passes by finding no evidence. That is not
    /// hypothetical: the first draft of this file asserted on bracket-quoted names and passed
    /// vacuously.
    /// </summary>
    private static string Normalize(string sql) =>
        sql.Replace("[", "", StringComparison.Ordinal)
           .Replace("]", "", StringComparison.Ordinal)
           .Replace("\"", "", StringComparison.Ordinal);

    /// <summary>
    /// True when a statement carries the free-text search predicate. The two providers spell it
    /// differently — SQL Server emits <c>LIKE</c>, SQLite emits <c>instr(...) &gt; 0</c> — so
    /// matching on only one of them finds nothing on the other.
    /// </summary>
    private static bool IsSearchStatement(string sql) =>
        sql.Contains("LIKE", StringComparison.OrdinalIgnoreCase)
        || sql.Contains("instr(", StringComparison.OrdinalIgnoreCase);

    /// <summary>Statements carrying the free-text search predicate.</summary>
    private static List<string> SearchCommands(CommandRecorder recorder) =>
        recorder.Commands.FindAll(IsSearchStatement);

    /// <summary>
    /// Sanity check on the matchers themselves. Every assertion below is a "no such statement"
    /// claim, so all of them would pass against a recorder that captured nothing at all.
    /// </summary>
    [Fact]
    public async Task Recorder_CapturesTheFeedsStatements()
    {
        var (service, recorder) = RecordingService();

        await service.GetGroupedFeedAsync(
            pageNumber: 1,
            pageSize: 25,
            userId: IntegrationTestSeeder.TestUserId,
            searchText: "needle");

        Assert.NotEmpty(recorder.Commands);
        Assert.NotEmpty(SearchCommands(recorder));
        Assert.Contains(recorder.Commands, c =>
            Normalize(c).Contains("ProjectId IS NULL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StandaloneBranch_DoesNotJoinProjects()
    {
        var (service, recorder) = RecordingService();

        await service.GetGroupedFeedAsync(
            pageNumber: 1,
            pageSize: 25,
            userId: IntegrationTestSeeder.TestUserId,
            searchText: "needle");

        // A statement that restricts to standalone prints has no use for the Projects table:
        // the row it would join to is NULL by construction.
        var standaloneJoins = recorder.Commands.FindAll(c =>
        {
            var sql = Normalize(c);
            return sql.Contains("ProjectId IS NULL", StringComparison.Ordinal)
                && sql.Contains("JOIN Projects", StringComparison.Ordinal);
        });

        Assert.True(
            standaloneJoins.Count == 0,
            "The standalone-print branch joined Projects, which it cannot match against:\n"
                + string.Join("\n---\n", standaloneJoins));
    }

    [Fact]
    public async Task TextSearch_IsEvaluatedAtMostOncePerBranch()
    {
        var (service, recorder) = RecordingService();

        await service.GetGroupedFeedAsync(
            pageNumber: 1,
            pageSize: 25,
            userId: IntegrationTestSeeder.TestUserId,
            searchText: "needle");

        // Two branches, one evaluation each: the per-project filtered counts and the standalone
        // print list. Filament totals resolve from ids those queries already returned, so a
        // third is the join-back creeping back in.
        var searchCommands = SearchCommands(recorder);
        Assert.True(
            searchCommands.Count <= 2,
            $"The text search ran {searchCommands.Count} times; at most 2 are needed:\n"
                + string.Join("\n---\n", searchCommands));
    }

    [Fact]
    public async Task StandaloneFilamentTotals_AreNotFetchedByReRunningTheSearch()
    {
        var (service, recorder) = RecordingService();

        // A standalone print with filament usage, so the totals query has rows to aggregate.
        var created = await service.AddPrint(
            new AddPrintDTO
            {
                Title = "Shape Probe needle",
                PrinterId = IntegrationTestSeeder.TestPrinterId,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
                AllowComments = false,
                FilamentUsage = new List<PrintFilamentSummaryDto>
                {
                    new PrintFilamentSummaryDto
                    {
                        Id = Guid.NewGuid(),
                        Filament = new FilamentSummaryDto { Id = IntegrationTestSeeder.TestFilamentId1 },
                        Source = PrintFilament.SourceMeasurement.Weight,
                        AmountMg = 4200,
                    },
                },
            },
            IntegrationTestSeeder.TestUserId);
        Assert.NotNull(created);

        recorder.Commands.Clear();

        await service.GetGroupedFeedAsync(
            pageNumber: 1,
            pageSize: 25,
            userId: IntegrationTestSeeder.TestUserId,
            searchText: "needle");

        // The totals query is identifiable by the table it aggregates. It must reach its rows
        // through print ids, never by repeating the text search.
        var filamentAggregates = recorder.Commands.FindAll(c =>
            Normalize(c).Contains("FROM PrintFilament", StringComparison.Ordinal)
            && c.Contains("SUM(", StringComparison.Ordinal));

        Assert.NotEmpty(filamentAggregates);
        Assert.All(filamentAggregates, sql =>
            Assert.False(
                IsSearchStatement(sql),
                "The filament totals query re-ran the text search: " + sql));
    }
}
