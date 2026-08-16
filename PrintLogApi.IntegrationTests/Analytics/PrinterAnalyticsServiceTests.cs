using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics;

public class PrinterAnalyticsServiceTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
{
    private readonly Mcp.McpDataWebApplicationFactory _factory;

    public PrinterAnalyticsServiceTests(Mcp.McpDataWebApplicationFactory factory) => _factory = factory;

    private async Task<PrintersResponse> Get(AnalyticsFilter filter)
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPrinterAnalyticsService>();
        filter.Normalize();
        return await service.GetPrinters(Mcp.McpTestData.MetricsUserId, filter, CancellationToken.None);
    }

    [Fact]
    public async Task GetPrinters_UtilizationIsNeverAboveOneHundredPercent()
    {
        var response = await Get(new AnalyticsFilter { TimeZone = "America/Chicago" });

        // Guard the precondition: Assert.All over an empty list passes and proves nothing.
        Assert.NotEmpty(response.Printers);
        var withUtilization = response.Printers.Where(p => p.UtilizationPercent.HasValue).ToList();
        Assert.NotEmpty(withUtilization);

        Assert.All(withUtilization, p => Assert.InRange(p.UtilizationPercent!.Value, 0, 100));

        Assert.NotNull(response.FleetUtilizationPercent.Value);
        Assert.InRange(response.FleetUtilizationPercent.Value!.Value, 0, 100);
    }

    [Fact]
    public async Task GetPrinters_IdlePrintersAreReturnedAsRowsAndExcludedFromTheFleetAverage()
    {
        var response = await Get(new AnalyticsFilter
        {
            TimeZone = "UTC",
            FromDate = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ToDate = new DateTimeOffset(1900, 2, 1, 0, 0, 0, TimeSpan.Zero),
        });

        // A window with no prints at all: every owned printer is idle, and there is no
        // fleet figure to report rather than a misleading 0%.
        Assert.All(response.Printers, p => Assert.True(p.IsIdle));
        Assert.All(response.Printers, p => Assert.Equal(0, p.PrintCount));
        Assert.Null(response.FleetUtilizationPercent.Value);
    }

    [Fact]
    public async Task GetPrinters_PerPrinterCountsSumToTheTotalForTheSameFilter()
    {
        var filter = new AnalyticsFilter { TimeZone = "UTC" };
        var response = await Get(filter);

        using var scope = _factory.Services.CreateScope();
        var overview = await scope.ServiceProvider.GetRequiredService<IAnalyticsService>()
            .GetOverview(Mcp.McpTestData.MetricsUserId, filter, CancellationToken.None);

        Assert.Equal(overview.Tiles.PrintCount.Value, response.Printers.Sum(p => p.PrintCount));
    }

    [Fact]
    public async Task GetPrinters_SuccessRateExcludesUnresolvedStatusesAndIsNullWhenNoneAreResolved()
    {
        var response = await Get(new AnalyticsFilter
        {
            TimeZone = "UTC",
            Statuses = { PrintLogApi.Models.Print.PrintStatus.Pending },
        });

        // Filtered to Pending only: the success-rate denominator is 0 everywhere, so the rate
        // must be suppressed rather than reported as 0%. Guard the precondition — the tenant
        // owns printers, so an empty list here would mean the test proved nothing.
        Assert.NotEmpty(response.Printers);
        Assert.All(response.Printers, p => Assert.Null(p.SuccessRatePercent));
    }

    /// <summary>
    /// A printer owned by the metrics user that exists only for the calling test.
    ///
    /// The fixture database is shared across the whole class, so seeding onto "the first
    /// owned printer" makes every assertion depend on ambient data — another test's rows, or
    /// a seeder change, silently shifts the expected total. A dedicated printer plus a filter
    /// pinned to it makes each of these tests answer for its own data and nothing else.
    /// </summary>
    private static async Task<PrintLogApi.Models.Printer> CreateScratchPrinter(PrintLogContext db)
    {
        var printer = new PrintLogApi.Models.Printer
        {
            UserId = Mcp.McpTestData.MetricsUserId,
            Name = $"scratch-{Guid.NewGuid():N}",
            Make = "Test",
            Model = "Scratch",
            IsActive = true,
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();
        return printer;
    }

    [Fact]
    public async Task GetPrinters_ReportsAnUnreadableMaintenancePriceRatherThanDroppingIt()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var printer = await CreateScratchPrinter(db);
        var entry = new PrintLogApi.Models.PrinterMaintenance
        {
            PrinterId = printer.Id,
            Done = true,
            Date = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero),
            Category = "Belt",
            PriceValue = "twenty five", // entered, unreadable — NOT the same as absent
            // PrinterMaintenance is a TimestampEntity: CreatedById/UpdatedById are real FKs
            // to User, and leaving them at 0 fails the foreign-key constraint on save.
            CreatedById = Mcp.McpTestData.MetricsUserId,
            UpdatedById = Mcp.McpTestData.MetricsUserId,
            CreatedDate = DateTime.UtcNow,
            UpdatedDate = DateTime.UtcNow,
        };
        db.PrinterMaintenance.Add(entry);
        await db.SaveChangesAsync();

        try
        {
            // Scoped to the scratch printer, so the count below is caused by THIS row and
            // cannot be satisfied by an unreadable price someone else's fixture left behind.
            var response = await Get(new AnalyticsFilter
            {
                TimeZone = "UTC",
                PrinterIds = { printer.Id },
            });

            var priceMissing = response.Coverage.Exclusions
                .FirstOrDefault(e => e.Reason == ExclusionReason.PriceMissing);

            Assert.NotNull(priceMissing);
            Assert.Equal(1, priceMissing.Count);

            // Unconditional: the one maintenance row here has no readable price, so the
            // printer's maintenance cost is unknown rather than zero.
            var row = Assert.Single(response.Printers);
            Assert.Null(row.MaintenanceCost);
        }
        finally
        {
            db.Remove(entry);
            db.Remove(printer);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task GetPrinters_CountsMaintenanceSpendBeyondTheEventDisplayCap()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var printer = await CreateScratchPrinter(db);

        // One more than the DISPLAY cap, each priced at 1.00. If the money were summed from
        // the capped event list, the total would stick at 500.
        var entries = Enumerable.Range(0, PrinterAnalyticsService.MaxMaintenanceEvents + 1)
            .Select(i => new PrintLogApi.Models.PrinterMaintenance
            {
                PrinterId = printer.Id,
                Done = true,
                Date = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
                Category = "Bulk",
                PriceValue = "1.00",
                CreatedById = Mcp.McpTestData.MetricsUserId,
                UpdatedById = Mcp.McpTestData.MetricsUserId,
                CreatedDate = DateTime.UtcNow,
                UpdatedDate = DateTime.UtcNow,
            })
            .ToList();

        db.PrinterMaintenance.AddRange(entries);
        await db.SaveChangesAsync();

        try
        {
            var response = await Get(new AnalyticsFilter
            {
                TimeZone = "UTC",
                FromDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                ToDate = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero),
                PrinterIds = { printer.Id },
            });

            // Exactly 501, on a printer that had nothing before this test ran. The cast is
            // required: MaintenanceCost is decimal?, and Assert.Equal cannot infer a single
            // T from (int, decimal?).
            var row = Assert.Single(response.Printers);
            Assert.Equal((decimal?)entries.Count, row.MaintenanceCost);
            // The event LIST is still capped, and says so.
            Assert.Equal(PrinterAnalyticsService.MaxMaintenanceEvents, response.Maintenance.Count);
            Assert.Contains(response.Coverage.Exclusions,
                e => e.Reason == ExclusionReason.RowCapExceeded);
        }
        finally
        {
            db.RemoveRange(entries);
            db.Remove(printer);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task GetPrinters_CountsAPrintThatStartedBeforeTheWindowButRanIntoIt()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var printer = await CreateScratchPrinter(db);

        // Starts one hour BEFORE the window and runs for ten hours, so nine of them fall
        // inside a 24-hour window. Selecting intervals by StartDate >= from drops this row
        // entirely and reports the printer as completely idle.
        var windowFrom = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero);
        var print = new PrintLogApi.Models.Print
        {
            Title = "Spans the window start",
            StartDate = windowFrom.AddHours(-1),
            PrintTimeInSeconds = 10 * 3600,
            Status = PrintLogApi.Models.Print.PrintStatus.Success,
            ViewStatus = PrintLogApi.Models.Print.PrintViewStatus.Private,
            PrinterId = printer.Id,
            CreatedById = Mcp.McpTestData.MetricsUserId,
            UpdatedById = Mcp.McpTestData.MetricsUserId,
            CreatedDate = DateTime.UtcNow,
            UpdatedDate = DateTime.UtcNow,
        };
        db.Prints.Add(print);
        await db.SaveChangesAsync();

        try
        {
            var response = await Get(new AnalyticsFilter
            {
                TimeZone = "UTC",
                FromDate = windowFrom,
                ToDate = windowFrom.AddDays(1),
                PrinterIds = { printer.Id },
            });

            var row = Assert.Single(response.Printers);

            // Nine hours of a 24-hour window = 37.5%. The pre-fix behaviour was 0%.
            Assert.NotNull(row.UtilizationPercent);
            Assert.Equal(37.5, row.UtilizationPercent!.Value, 1);
        }
        finally
        {
            db.Remove(print);
            db.Remove(printer);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task GetPrinters_SuppressesUtilizationAndAverageWhenNoDurationWasEverRecorded()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var printer = await CreateScratchPrinter(db);

        // A real print with NO duration. "We do not know how long it ran" must not be
        // reported as 0% utilization or averaged into the fleet as an idle machine.
        var print = new PrintLogApi.Models.Print
        {
            Title = "No duration recorded",
            StartDate = new DateTimeOffset(2026, 5, 10, 6, 0, 0, TimeSpan.Zero),
            PrintTimeInSeconds = null,
            EstimatedPrintTimeInSeconds = null,
            Status = PrintLogApi.Models.Print.PrintStatus.Success,
            ViewStatus = PrintLogApi.Models.Print.PrintViewStatus.Private,
            PrinterId = printer.Id,
            CreatedById = Mcp.McpTestData.MetricsUserId,
            UpdatedById = Mcp.McpTestData.MetricsUserId,
            CreatedDate = DateTime.UtcNow,
            UpdatedDate = DateTime.UtcNow,
        };
        db.Prints.Add(print);
        await db.SaveChangesAsync();

        try
        {
            var response = await Get(new AnalyticsFilter
            {
                TimeZone = "UTC",
                FromDate = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero),
                ToDate = new DateTimeOffset(2026, 5, 11, 0, 0, 0, TimeSpan.Zero),
                PrinterIds = { printer.Id },
            });

            var row = Assert.Single(response.Printers);

            Assert.Equal(1, row.PrintCount);
            Assert.False(row.IsIdle);
            Assert.Null(row.UtilizationPercent);
            Assert.Null(row.AvgDurationSeconds);
            // With no measurable printer, there is no fleet figure to report either.
            Assert.Null(response.FleetUtilizationPercent.Value);
        }
        finally
        {
            db.Remove(print);
            db.Remove(printer);
            await db.SaveChangesAsync();
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(AnalyticsService.MaxSeriesRows, false)]
    [InlineData(AnalyticsService.MaxSeriesRows + 1, true)]
    public void MaintenanceTotals_RowCapBoundaryIsInclusive(int rowCount, bool skipped)
    {
        // The 20,000-row guard tested directly rather than by seeding 20,001 rows. Extracting
        // the predicate is what makes the boundary pinnable at all; the cap test above only
        // crosses the 500-row DISPLAY cap and says nothing about this one.
        Assert.Equal(skipped, PrinterAnalyticsService.ShouldSkipMaintenanceTotals(rowCount));
    }

    [Fact]
    public async Task GetPrinters_AnUnownedPrinterFilterReturnsNoRowsRatherThanAnError()
    {
        var response = await Get(new AnalyticsFilter
        {
            TimeZone = "UTC",
            PrinterIds = { long.MaxValue },
        });

        Assert.Empty(response.Printers);
    }

    [Fact]
    public async Task GetPrinters_NeverReturnsAPrinterBelongingToAnotherUser()
    {
        var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var ownedIds = db.Printers
            .Where(p => p.UserId == Mcp.McpTestData.MetricsUserId)
            .Select(p => p.Id).ToList();

        Assert.All(response.Printers, p => Assert.Contains(p.PrinterId, ownedIds));
    }
}
