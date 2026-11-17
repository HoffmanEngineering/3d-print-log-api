using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// Two overlapping finishes of one print (#199): both read it unfinished, and only one may merge
/// usage and notify. The other must end like a later replay of a finished print, not a 500.
/// </summary>
/// <remarks>
/// Built deterministically rather than with concurrent requests, for the reason
/// <see cref="PrintServiceExternalIdRaceTests"/> gives: an interceptor runs the whole competing
/// finish from another scope after the losing request has read the print as unfinished and before
/// it writes anything.
/// </remarks>
public class PrintFinishRaceTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NotifierFinish_LosesTheRace_ReplaysWithoutErrorOrSecondNotification()
    {
        var file = $"finish-race-{Guid.NewGuid():N}.gcode";
        var printId = await AddPrintingPrintAsync(file, withStartedRow: true);
        // Read after the first use of factory.Services, which is what seeds it.
        var userId = IntegrationTestSeeder.TestUserId;
        await AddAccumulationsAsync(printId,
            (IntegrationTestSeeder.TestFilamentId1, 1000),
            (IntegrationTestSeeder.TestFilamentId2, 500));
        var finished = new PrintFinishedEvent
        {
            UserId = userId,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FileName = file,
            Status = PrintStatus.Success,
            PrintTimeInSeconds = 120,
            ActualLengthInM = 9.999,
        };

        using var scope = factory.Services.CreateScope();
        // The losing request has read the print, as Printing, and goes for its Spoolman reports.
        var race = new BeforeFirstReaderInterceptor("FROM \"SpoolmanUsages\"", async () =>
        {
            using var other = factory.Services.CreateScope();
            Assert.NotNull(await other.ServiceProvider.GetRequiredService<IPrintEventService>().Finished(finished));
        });
        await using var db = RacingContext(scope, race);
        var loser = ActivatorUtilities.CreateInstance<PrintEventService>(
            scope.ServiceProvider,
            db,
            ActivatorUtilities.CreateInstance<PrintService>(scope.ServiceProvider, db));

        // Before the fix, its RemoveRange of reports the winner had already deleted threw
        // DbUpdateConcurrencyException: a 500 to the notifier.
        var result = await loser.Finished(finished);

        Assert.True(race.Fired, "the competing finish never ran, so no race was tested");
        Assert.Null(result);
        Assert.Single(await NotificationsForPrintAsync(printId));
        var rows = await UsageRowsAsync(printId);
        Assert.Equal(2, rows.Count);
        Assert.Equal(
            [IntegrationTestSeeder.TestFilamentId1, IntegrationTestSeeder.TestFilamentId2],
            rows.Select(r => r.FilamentId!.Value).Order());
        Assert.Equal(PrintStatus.Success, await StatusAsync(printId));
    }

    [Fact]
    public async Task Complete_LosesTheRace_ReplaysWithoutDuplicateRowOrSecondNotification()
    {
        var printId = await AddPrintingPrintAsync($"complete-race-{Guid.NewGuid():N}.gcode", withStartedRow: false);
        // Read after the first use of factory.Services, which is what seeds it.
        var userId = IntegrationTestSeeder.TestUserId;
        // A filament no row has yet: each finish that merges it adds a row.
        var completion = new CompletePrintDto
        {
            Status = PrintStatus.Success,
            PrintTimeInSeconds = 600,
            FilamentUsage = [new CompletePrintFilamentUsageDto { FilamentId = IntegrationTestSeeder.TestFilamentId1, LengthInM = 2 }],
        };

        using var scope = factory.Services.CreateScope();
        // The losing request has read the print, as Printing, and looks up the printer's slots.
        var race = new BeforeFirstReaderInterceptor("\"SlotCount\"", async () =>
        {
            using var other = factory.Services.CreateScope();
            await other.ServiceProvider.GetRequiredService<IPrintService>().CompletePrint(printId, completion, userId);
        });
        await using var db = RacingContext(scope, race);
        var loser = ActivatorUtilities.CreateInstance<PrintService>(scope.ServiceProvider, db);

        var result = await loser.CompletePrint(printId, completion, userId);

        Assert.True(race.Fired, "the competing completion never ran, so no race was tested");
        Assert.Equal(PrintStatus.Success, result.Status);
        Assert.Equal(600, result.PrintTimeInSeconds);
        Assert.Single(await NotificationsForPrintAsync(printId));
        var row = Assert.Single(await UsageRowsAsync(printId));
        Assert.Equal(IntegrationTestSeeder.TestFilamentId1, row.FilamentId);
        Assert.Equal(2, row.LengthInM);
    }

    private PrintLogContext RacingContext(IServiceScope scope, IInterceptor race)
    {
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<PrintLogContext>>();
        return new PrintLogContext(new DbContextOptionsBuilder<PrintLogContext>(options).AddInterceptors(race).Options);
    }

    private async Task<long> AddPrintingPrintAsync(string file, bool withStartedRow)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = new Print
        {
            Title = "Racing",
            FileName = file,
            StartDate = DateTimeOffset.UtcNow,
            Status = PrintStatus.Printing,
            ViewStatus = PrintViewStatus.Private,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            CreatedById = IntegrationTestSeeder.TestUserId,
            UpdatedById = IntegrationTestSeeder.TestUserId,
            FilamentUsage = withStartedRow
                ?
                [
                    new PrintFilament
                    {
                        FilamentId = IntegrationTestSeeder.TestFilamentId1,
                        Slot = 0,
                        EstimatedSource = PrintFilament.SourceMeasurement.Length,
                        EstimatedLengthInM = 0,
                        Source = PrintFilament.SourceMeasurement.Length,
                        LengthInM = 0,
                    },
                ]
                : [],
        };
        db.Prints.Add(print);
        await db.SaveChangesAsync(Ct);
        return print.Id;
    }

    private async Task AddAccumulationsAsync(long printId, params (Guid FilamentId, double LengthMm)[] reports)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        foreach (var (filamentId, lengthMm) in reports)
        {
            db.SpoolmanUsages.Add(new SpoolmanUsage
            {
                PrintId = printId,
                FilamentId = filamentId,
                LengthMm = lengthMm,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync(Ct);
    }

    private async Task<List<Notification>> NotificationsForPrintAsync(long printId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Notifications.AsNoTracking().Where(n => n.PrintId == printId).ToListAsync(Ct);
    }

    private async Task<List<PrintFilament>> UsageRowsAsync(long printId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.PrintFilament.AsNoTracking().Where(pf => pf.PrintId == printId).ToListAsync(Ct);
    }

    private async Task<PrintStatus> StatusAsync(long printId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Prints.Where(p => p.Id == printId).Select(p => p.Status).SingleAsync(Ct);
    }

    /// <summary>Runs <paramref name="before"/> once, just before the first query whose SQL contains <paramref name="marker"/>.</summary>
    private sealed class BeforeFirstReaderInterceptor(string marker, Func<Task> before) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains(marker, StringComparison.Ordinal))
            {
                Fired = true;
                await before();
            }
            return result;
        }
    }
}
