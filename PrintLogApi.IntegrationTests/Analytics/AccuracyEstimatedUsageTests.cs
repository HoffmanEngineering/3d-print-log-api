using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics;

/// <summary>
/// Usage rows whose actual is the slicer's estimate (<see cref="PrintFilament.ActualIsEstimate"/>,
/// printlog-bridge#82) stay out of the material accuracy sample: compared with the estimate they
/// came from, they would always look perfect. Each test seeds its own user, so the shared
/// fixtures' totals are untouched.
/// </summary>
public class AccuracyEstimatedUsageTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private const int EstimatedMg = 10_000;
    private const int MeasuredMg = 12_000;

    [Fact]
    public async Task EstimatedActuals_AreLeftOutOfTheMaterialMedian()
    {
        var userId = await SeedAsync(measuredPrints: 6, estimatedPrints: 6);

        var response = await GetAsync(userId);

        // Measured prints alone are all 1.2; the estimated ones would pull the median to 1.1.
        Assert.NotNull(response.MaterialAccuracyMedian.Value);
        Assert.InRange(response.MaterialAccuracyMedian.Value!.Value, 1.19, 1.21);
        Assert.Equal(6, response.MaterialAccuracyMedian.Coverage.Counted);
    }

    [Fact]
    public async Task EstimatedActuals_AreLeftOutOfTheByMaterialGroups()
    {
        var userId = await SeedAsync(measuredPrints: 6, estimatedPrints: 6);

        var response = await GetAsync(userId);

        var pla = Assert.Single(response.ByMaterial);
        Assert.Equal(6, pla.SampleSize);
        Assert.NotNull(pla.MedianRatio);
        Assert.InRange(pla.MedianRatio!.Value, 1.19, 1.21);
    }

    [Fact]
    public async Task APrintWithOnlyEstimatedActuals_HasNoMaterialSample_ButKeepsItsTimeSample()
    {
        var userId = await SeedAsync(measuredPrints: 0, estimatedPrints: 6);

        var response = await GetAsync(userId);

        Assert.Null(response.MaterialAccuracyMedian.Value);
        Assert.Equal(0, response.MaterialAccuracyMedian.Coverage.Counted);
        Assert.Empty(response.ByMaterial);
        // Durations are measured whatever the firmware reports about filament.
        Assert.Equal(6, response.TimeAccuracyMedian.Coverage.Counted);
    }

    [Fact]
    public async Task OnAMixedPrint_OnlyTheMeasuredRowCounts()
    {
        var userId = await SeedAsync(measuredPrints: 0, estimatedPrints: 0, mixedPrints: 6);

        var response = await GetAsync(userId);

        Assert.NotNull(response.MaterialAccuracyMedian.Value);
        Assert.InRange(response.MaterialAccuracyMedian.Value!.Value, 1.19, 1.21);
    }

    private async Task<AccuracyResponse> GetAsync(long userId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAccuracyAnalyticsService>();
        var filter = new AnalyticsFilter { TimeZone = "UTC" };
        filter.Normalize();
        return await service.GetAccuracy(userId, filter, TestContext.Current.CancellationToken);
    }

    private async Task<long> SeedAsync(int measuredPrints, int estimatedPrints, int mixedPrints = 0)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var ct = TestContext.Current.CancellationToken;

        var user = new User { OAuthUserId = $"auth0|accuracy-estimated-{Guid.NewGuid():N}", ViewStatus = User.ProfileViewStatus.Private };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        var printer = new Printer { Name = "Adapter printer", Make = "Test", Model = "Estimate", UserId = user.Id, IsActive = true };
        db.Printers.Add(printer);
        var spool = new Filament
        {
            Id = Guid.NewGuid(),
            Brand = "Test",
            ColorHex = "00FF00",
            ColorName = "Green",
            DisplayName = "Accuracy PLA",
            MaterialType = "PLA",
            MaterialCategoryNickname = "filament",
            MaterialDensityGramPerCubicCm = 1.24,
            DiameterMm = 1.75,
            IsActive = true,
            InitialNominalWeightMg = 1_000_000,
            Source = Filament.SourceMeasurement.Weight,
            CreatedById = user.Id,
            UpdatedById = user.Id,
        };
        db.Filaments.Add(spool);
        await db.SaveChangesAsync(ct);

        PrintFilament Row(bool estimated) => new()
        {
            Id = Guid.NewGuid(),
            FilamentId = spool.Id,
            EstimatedSource = PrintFilament.SourceMeasurement.Weight,
            EstimatedAmountMg = EstimatedMg,
            Source = PrintFilament.SourceMeasurement.Weight,
            // An adapter that only knows the estimate sends it as the actual.
            AmountMg = estimated ? EstimatedMg : MeasuredMg,
            ActualIsEstimate = estimated,
        };

        void Add(params PrintFilament[] rows) => db.Prints.Add(new Print
        {
            Title = "Adapter print",
            StartDate = DateTimeOffset.UtcNow.AddDays(-1),
            Status = Print.PrintStatus.Success,
            ViewStatus = Print.PrintViewStatus.Private,
            PrinterId = printer.Id,
            CreatedById = user.Id,
            UpdatedById = user.Id,
            EstimatedPrintTimeInSeconds = 3_600,
            PrintTimeInSeconds = 3_600,
            FilamentUsage = rows.ToList(),
        });

        for (var i = 0; i < measuredPrints; i++) Add(Row(estimated: false));
        for (var i = 0; i < estimatedPrints; i++) Add(Row(estimated: true));
        for (var i = 0; i < mixedPrints; i++) Add(Row(estimated: false), Row(estimated: true));
        await db.SaveChangesAsync(ct);
        return user.Id;
    }
}
