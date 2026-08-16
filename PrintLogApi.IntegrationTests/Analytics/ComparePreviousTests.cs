using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics;

public class ComparePreviousTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
{
    private readonly Mcp.McpDataWebApplicationFactory _factory;

    public ComparePreviousTests(Mcp.McpDataWebApplicationFactory factory) => _factory = factory;

    private static AnalyticsFilter Ranged(bool compare) => new()
    {
        TimeZone = "UTC",
        FromDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        ToDate = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        ComparePrevious = compare,
    };

    [Fact]
    public async Task Costs_PopulatesPreviousOnlyWhenCompareIsRequested()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICostAnalyticsService>();

        var without = Ranged(false);
        without.Normalize();
        var with = Ranged(true);
        with.Normalize();

        var plain = await service.GetCosts(Mcp.McpTestData.MetricsUserId, without, CancellationToken.None);
        var compared = await service.GetCosts(Mcp.McpTestData.MetricsUserId, with, CancellationToken.None);

        Assert.Null(plain.TotalSpend.Previous);
        // The current-window figure must be identical either way: comparing must not change
        // what is being compared.
        Assert.Equal(plain.TotalSpend.Value, compared.TotalSpend.Value);

        // And the delta must actually arrive. Asserting only "null when off" passes with the
        // whole feature unimplemented, which is exactly how this shipped broken before.
        var prior = new AnalyticsFilter
        {
            TimeZone = "UTC",
            FromDate = new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero),
            ToDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };
        prior.Normalize();
        var priorResponse = await service.GetCosts(
            Mcp.McpTestData.MetricsUserId, prior, CancellationToken.None);

        Assert.Equal(
            PreviousWindow.Usable(priorResponse.TotalSpend.Value),
            compared.TotalSpend.Previous);
    }

    [Fact]
    public void PreviousWindow_IsAWholeLocalMonthEvenAcrossASpringForward()
    {
        // US DST 2026 begins Sunday 8 March. A local March window is 743 UTC hours, so a
        // UTC-span subtraction would put the prior window's start an hour off local midnight.
        var filter = new AnalyticsFilter
        {
            TimeZone = "America/Chicago",
            FromDate = new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero),  // 1 Mar local
            ToDate = new DateTimeOffset(2026, 3, 31, 5, 0, 0, TimeSpan.Zero),   // 31 Mar local
            ComparePrevious = true,
        };
        filter.Normalize();

        var previous = PreviousWindow.For(filter, DateTimeOffset.UtcNow);
        Assert.NotNull(previous);

        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");

        var localStart = TimeZoneInfo.ConvertTime(previous.FromDate!.Value, zone);
        var localEnd = TimeZoneInfo.ConvertTime(previous.ToDate!.Value, zone);

        // Both ends land on local midnight, and the window is the same number of LOCAL days.
        Assert.Equal(TimeSpan.Zero, localStart.TimeOfDay);
        Assert.Equal(TimeSpan.Zero, localEnd.TimeOfDay);
        Assert.Equal(30, (localEnd.Date - localStart.Date).TotalDays);
    }

    [Fact]
    public void PreviousWindow_EndsExactlyWhereTheCurrentWindowBegins()
    {
        // Adjacency across the FALL-BACK transition, where 01:30 local happens twice.
        // US DST 2025 ended Sunday 2 November. A window opening at the SECOND occurrence must
        // still be met exactly by its predecessor's end — a reconstructed boundary would
        // land on the first occurrence and leave an hour unaccounted for in both windows.
        //
        // A PAST transition deliberately: Normalize() clamps any range that reaches beyond
        // now, so a future November would be rewritten before PreviousWindow ever saw it and
        // the test would assert against a window it did not choose.
        var filter = new AnalyticsFilter
        {
            TimeZone = "America/Chicago",
            FromDate = new DateTimeOffset(2025, 11, 2, 7, 30, 0, TimeSpan.Zero), // 01:30 CST
            ToDate = new DateTimeOffset(2025, 12, 1, 6, 0, 0, TimeSpan.Zero),
            ComparePrevious = true,
        };
        filter.Normalize();

        var previous = PreviousWindow.For(filter, DateTimeOffset.UtcNow);

        Assert.NotNull(previous);
        Assert.Equal(filter.FromDate, previous.ToDate);
    }

    [Fact]
    public async Task Materials_SuppressesTheDeltaForAnAllTimeQuery()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMaterialAnalyticsService>();

        var filter = new AnalyticsFilter { TimeZone = "UTC", ComparePrevious = true };
        filter.Normalize();

        var response = await service.GetMaterials(
            Mcp.McpTestData.MetricsUserId, filter, CancellationToken.None);

        // All-time has no preceding window; a delta against nothing is not a number.
        Assert.Null(response.WasteGrams.Previous);
        Assert.Null(response.WasteCost.Previous);

        // But a RANGED query with comparison on does populate it, so the assertion above is
        // testing the all-time rule rather than an unimplemented feature.
        var ranged = Ranged(true);
        ranged.Normalize();
        var rangedResponse = await service.GetMaterials(
            Mcp.McpTestData.MetricsUserId, ranged, CancellationToken.None);

        var prior = new AnalyticsFilter
        {
            TimeZone = "UTC",
            FromDate = new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero),
            ToDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };
        prior.Normalize();
        var priorResponse = await service.GetMaterials(
            Mcp.McpTestData.MetricsUserId, prior, CancellationToken.None);

        Assert.Equal(
            PreviousWindow.Usable(priorResponse.WasteGrams.Value),
            rangedResponse.WasteGrams.Previous);
    }

    [Fact]
    public async Task Printers_PreviousWindowIsTheImmediatelyPrecedingRangeOfEqualLength()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPrinterAnalyticsService>();

        var current = Ranged(true);
        current.Normalize();
        var currentResponse = await service.GetPrinters(
            Mcp.McpTestData.MetricsUserId, current, CancellationToken.None);

        // The prior window computed directly, as its own current window.
        var prior = new AnalyticsFilter
        {
            TimeZone = "UTC",
            FromDate = new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero),
            ToDate = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };
        prior.Normalize();
        var priorResponse = await service.GetPrinters(
            Mcp.McpTestData.MetricsUserId, prior, CancellationToken.None);

        Assert.Equal(
            PreviousWindow.Usable(priorResponse.FleetUtilizationPercent.Value),
            currentResponse.FleetUtilizationPercent.Previous);
    }

    /// <summary>
    /// Pins the /overview migration onto PreviousWindow. Deriving the expected value FROM
    /// PreviousWindow.For would be circular — both sides would be wrong identically — so the
    /// expected window is stated by hand and a print is seeded in the one-hour slice where
    /// the local-calendar and UTC-span windows disagree.
    /// </summary>
    [Fact]
    public async Task Overview_UsesTheLocalCalendarPreviousWindowNotAUtcSpan()
    {
        using var scope = _factory.Services.CreateScope();
        var analytics = scope.ServiceProvider.GetRequiredService<IAnalyticsService>();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var printerId = await db.Printers
            .Where(p => p.UserId == Mcp.McpTestData.MetricsUserId)
            .Select(p => p.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Current window: local April 2026, i.e. entirely after the 8 March DST start.
        var filter = new AnalyticsFilter
        {
            TimeZone = "America/Chicago",
            FromDate = new DateTimeOffset(2026, 4, 1, 5, 0, 0, TimeSpan.Zero),   // 1 Apr 00:00 CDT
            ToDate = new DateTimeOffset(2026, 5, 1, 5, 0, 0, TimeSpan.Zero),     // 1 May 00:00 CDT
            ComparePrevious = true,
        };
        filter.Normalize();

        // The previous window, written out by hand: 30 LOCAL days back from 1 Apr 00:00 CDT
        // is 2 Mar 00:00 CST = 06:00Z. A UTC-span subtraction gives 2 Mar 05:00Z instead,
        // because the current window is 30 * 24 UTC hours and March lost an hour.
        var expectedPreviousStart = new DateTimeOffset(2026, 3, 2, 6, 0, 0, TimeSpan.Zero);
        var utcSpanPreviousStart = new DateTimeOffset(2026, 3, 2, 5, 0, 0, TimeSpan.Zero);
        Assert.NotEqual(expectedPreviousStart, utcSpanPreviousStart); // the slice exists

        // A print inside that one-hour slice: the OLD implementation includes it in the
        // previous window, the NEW one does not. This single row is what makes the assertion
        // able to fail — without it both implementations report the same count.
        var boundaryPrint = new PrintLogApi.Models.Print
        {
            CreatedById = Mcp.McpTestData.MetricsUserId,
            PrinterId = printerId,
            Title = "DST boundary probe",
            Status = PrintLogApi.Models.Print.PrintStatus.Success,
            ViewStatus = PrintLogApi.Models.Print.PrintViewStatus.Private,
            StartDate = utcSpanPreviousStart.AddMinutes(30), // 05:30Z: inside the old window only
            // Print is a TimestampEntity: UpdatedById defaults to 0, which is not a real user
            // and trips the foreign key.
            CreatedDate = DateTime.UtcNow,
            UpdatedById = Mcp.McpTestData.MetricsUserId,
            UpdatedDate = DateTime.UtcNow,
        };
        db.Prints.Add(boundaryPrint);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        try
        {
            var compared = await analytics.GetOverview(
                Mcp.McpTestData.MetricsUserId, filter, CancellationToken.None);

            // The expected prior count, from a window stated independently of PreviousWindow.
            var independentPrior = new AnalyticsFilter
            {
                TimeZone = "America/Chicago",
                FromDate = expectedPreviousStart,
                ToDate = filter.FromDate,
            };
            independentPrior.Normalize();

            var prior = await analytics.GetOverview(
                Mcp.McpTestData.MetricsUserId, independentPrior, CancellationToken.None);

            Assert.Equal(prior.Tiles.PrintCount.Value, compared.Tiles.PrintCount.Previous);

            // And prove the boundary print actually discriminates: the UTC-span window would
            // have counted one more print than the local-calendar window does.
            var utcSpanWindow = new AnalyticsFilter
            {
                TimeZone = "America/Chicago",
                FromDate = utcSpanPreviousStart,
                ToDate = filter.FromDate,
            };
            utcSpanWindow.Normalize();

            var wrong = await analytics.GetOverview(
                Mcp.McpTestData.MetricsUserId, utcSpanWindow, CancellationToken.None);

            Assert.NotEqual(wrong.Tiles.PrintCount.Value, compared.Tiles.PrintCount.Previous);
        }
        finally
        {
            db.Remove(boundaryPrint);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }
}
