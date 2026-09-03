using System.Data.Common;
using AutoMapper;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Models;
using PrintLogApi.Models.SortEnums;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// Proves the production service actually issues the binary-collation predicate.
///
/// This exists because the rest of the suite does not prove it. PrintSearchTranslationTests calls
/// PrintSearchPredicate directly and PrintSearchSemanticsTests builds its own query, so both stay
/// green if every call site in PrintService is reverted to the old inline
/// <c>p.Title.Contains(text) || p.Notes.Contains(text)</c> — verified by doing exactly that, at
/// which point all 1452 tests still passed. The helper was covered; the wiring was not.
///
/// So these assertions read the SQL the service really executed. They are deliberately spelled
/// against the collation NAME rather than the whole predicate: that name cannot appear unless the
/// statement came through PrintSearchPredicate, and it survives the provider difference (SQLite
/// emits instr(...) where SQL Server emits LIKE, but both carry the COLLATE).
/// </summary>
public class PrintSearchWiringTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PrintSearchWiringTests(CustomWebApplicationFactory factory) => _factory = factory;

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
    /// A PrintService over the same seeded connection the factory uses, with a recording
    /// interceptor on its context — the shape GroupedFeedQueryShapeTests and AnalyticsRoundTripTests
    /// already use, for the same reason: the interceptor must attach without changing how the
    /// application registers its context.
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

    /// <summary>True when a statement carries the free-text search predicate.</summary>
    private static bool IsSearchStatement(string sql) =>
        sql.Contains("LIKE", StringComparison.OrdinalIgnoreCase)
        || sql.Contains("instr(", StringComparison.OrdinalIgnoreCase);

    private static void AssertEverySearchStatementIsCollated(CommandRecorder recorder)
    {
        var searchCommands = recorder.Commands.FindAll(IsSearchStatement);

        // Without this the assertion below is a claim about an empty list, which is how a wiring
        // test quietly stops testing anything.
        Assert.NotEmpty(searchCommands);

        var uncollated = searchCommands.FindAll(c =>
            !c.Contains(PrintSearchPredicate.BinaryCollation, StringComparison.Ordinal));

        Assert.True(
            uncollated.Count == 0,
            $"{uncollated.Count} of {searchCommands.Count} search statements did not go through "
                + "PrintSearchPredicate:\n" + string.Join("\n---\n", uncollated));
    }

    [Fact]
    public async Task SearchPrintSummary_SearchesThroughTheBinaryCollation()
    {
        var (service, recorder) = RecordingService();

        await service.SearchPrintSummary(
            new PagedRequest { PageNumber = 1, PageSize = 10 },
            searchText: "needle",
            new SortRequest<PrintSummarySortColumn>(),
            filterByPrinterIds: null,
            filterByFilamentIds: null,
            statuses: null,
            userId: IntegrationTestSeeder.TestUserId,
            currentUserId: IntegrationTestSeeder.TestUserId);

        AssertEverySearchStatementIsCollated(recorder);
    }

    [Fact]
    public async Task GroupedFeed_SearchesBothBranchesThroughTheBinaryCollation()
    {
        var (service, recorder) = RecordingService();

        await service.GetGroupedFeedAsync(
            pageNumber: 1,
            pageSize: 25,
            userId: IntegrationTestSeeder.TestUserId,
            searchText: "needle");

        // Both branches — the project-assigned query and the standalone one — reach this via
        // different helper overloads, so a conversion that missed the standalone site (as #110's
        // own line references would have led one to do) fails here.
        AssertEverySearchStatementIsCollated(recorder);
    }
}
