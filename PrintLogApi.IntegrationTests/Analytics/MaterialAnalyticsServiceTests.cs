using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics;

public class MaterialAnalyticsServiceTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
{
    private readonly Mcp.McpDataWebApplicationFactory _factory;

    public MaterialAnalyticsServiceTests(Mcp.McpDataWebApplicationFactory factory) => _factory = factory;

    private async Task<MaterialsResponse> Get(AnalyticsFilter filter)
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMaterialAnalyticsService>();
        filter.Normalize();
        return await service.GetMaterials(Mcp.McpTestData.MetricsUserId, filter, CancellationToken.None);
    }

    [Fact]
    public async Task GetMaterials_GroupTotalsFallShortOfTheOverviewTotalByExactlyTheOtherFilamentScalar()
    {
        var filter = new AnalyticsFilter { TimeZone = "UTC" };
        var response = await Get(filter);

        using var scope = _factory.Services.CreateScope();
        var overview = await scope.ServiceProvider.GetRequiredService<IAnalyticsService>()
            .GetOverview(Mcp.McpTestData.MetricsUserId, filter, CancellationToken.None);

        var grouped = response.ByType.Sum(g => g.MaterialMg);
        var overviewMg = (long)Math.Round((overview.Tiles.FilamentGrams.Value ?? 0) * 1000);

        // "Other filament" has no spool, so it has no type, brand or colour and cannot be
        // grouped. The shortfall is exactly the legacy scalar, and it is NOT a bug.
        Assert.Equal(Mcp.McpTestData.LegacyMaterialMatrixTotalMg, overviewMg - grouped);

        // And the shortfall is EXPLAINED, not silent.
        Assert.Contains(response.Coverage.Exclusions,
            e => e.Reason == ExclusionReason.UnattributedMaterial && e.Count > 0);
    }

    [Fact]
    public async Task GetMaterials_RemainingMatchesTheCanonicalFilamentSummaryRule()
    {
        var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var mapper = scope.ServiceProvider.GetRequiredService<IMapper>();

        var canonical = await db.Filaments.AsNoTracking()
            .Where(f => f.CreatedById == Mcp.McpTestData.MetricsUserId)
            .ProjectTo<FilamentSummaryDto>(mapper.ConfigurationProvider)
            .ToDictionaryAsync(f => f.Id, f => f.FilamentRemaining, cancellationToken: TestContext.Current.CancellationToken);

        // Guard both preconditions: a `continue` past every row, or an empty TopSpools,
        // would let this test pass while comparing nothing at all.
        Assert.NotEmpty(response.TopSpools);

        var compared = 0;
        foreach (var spool in response.TopSpools)
        {
            var id = Guid.Parse(spool.FilamentId);
            Assert.True(canonical.ContainsKey(id),
                $"spool {id} is in TopSpools but not in the tenant's own filaments");
            Assert.Equal(canonical[id], spool.RemainingMg);
            compared++;
        }

        Assert.Equal(response.TopSpools.Count, compared);
    }

    [Fact]
    public async Task GetMaterials_PricesConsumedMaterialThroughTheSharedCalculator()
    {
        var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

        Assert.NotEmpty(response.TopSpools);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var inputs = await AnalyticsCostProjection.LoadInputs(
            db, Mcp.McpTestData.MetricsUserId, CancellationToken.None);

        foreach (var spool in response.TopSpools)
        {
            var filament = await db.Filaments.AsNoTracking()
                .FirstAsync(f => f.Id == Guid.Parse(spool.FilamentId), cancellationToken: TestContext.Current.CancellationToken);

            var priceable = !string.IsNullOrWhiteSpace(filament.PurchasePriceValue)
                || !string.IsNullOrWhiteSpace(inputs.DefaultFilamentPrice);
            var weighable = filament.InitialNominalWeightMg is > 0;

            // A spool that cannot be priced reports null, never a confident 0.00.
            if (priceable && weighable) Assert.NotNull(spool.CostConsumed);
            else Assert.Null(spool.CostConsumed);
        }
    }

    [Fact]
    public async Task GetMaterials_WasteCountsFailedAndCancelledButNotPartialSuccess()
    {
        var failedAndCancelled = await Get(new AnalyticsFilter
        {
            TimeZone = "UTC",
            Statuses =
            {
                PrintLogApi.Models.Print.PrintStatus.Failed,
                PrintLogApi.Models.Print.PrintStatus.Cancelled,
            },
        });
        var everything = await Get(new AnalyticsFilter { TimeZone = "UTC" });

        // Filtering the whole tab to failed+cancelled makes waste equal total consumption.
        Assert.Equal(
            failedAndCancelled.ByType.Sum(g => g.MaterialMg) / 1000.0,
            failedAndCancelled.WasteGrams.Value ?? 0,
            3);

        Assert.True((everything.WasteGrams.Value ?? 0) <= everything.ByType.Sum(g => g.MaterialMg) / 1000.0 + 0.001);
    }

    [Fact]
    public async Task GetMaterials_RunwayIsSuppressedWhenBurnRateIsZeroAndNeverExceedsAYear()
    {
        var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

        Assert.All(response.Runway, row =>
        {
            if (row.BurnRateGramsPerDay <= 0) Assert.Null(row.RunwayDays);
            if (row.RunwayDays.HasValue) Assert.InRange(row.RunwayDays.Value, 0, 365);
        });
    }

    [Fact]
    public async Task GetMaterials_CapsGroupsAndRollsTheRemainderIntoOther()
    {
        var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

        Assert.True(response.ByBrand.Count <= 16, $"{response.ByBrand.Count} brand rows");
        if (response.ByBrand.Count == 16)
            Assert.Equal("Other", response.ByBrand.Last().Label);
    }

    [Fact]
    public async Task GetMaterials_TimeSeriesKeysNeverEscapeTheTruncatedGroupKeySet()
    {
        var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

        // The UI builds one chart series per ByType entry and reads bucket values by that
        // key. A bucket key outside the set is a series the chart never draws — silently
        // dropping mass from a stacked chart the user reads as a total.
        var known = response.ByType.Select(g => g.Key).ToHashSet();

        Assert.All(response.ConsumptionOverTime, bucket =>
            Assert.All(bucket.MaterialMgByType.Keys, key => Assert.Contains(key, known)));
    }

    [Fact]
    public async Task GetMaterials_TimeSeriesConservesTheSameMassAsTheGroupTotals()
    {
        var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

        var grouped = response.ByType.Sum(g => g.MaterialMg);
        var bucketed = response.ConsumptionOverTime.Sum(b => b.MaterialMgByType.Values.Sum());

        // Equal because the metrics fixture's spool-attached prints are all dated. Undated
        // usage is excluded from time buckets by design, so this holds only while that is
        // true of the fixture — which GetMaterials_... coverage assertions also rely on.
        Assert.Equal(grouped, bucketed);
    }

    /// <summary>
    /// The group cap only bites above MaxGroups, and the shared metrics fixture has two
    /// material types — so this test seeds its OWN isolated user with one more type than the
    /// cap. Without that, every truncation assertion here passes vacuously.
    /// </summary>
    [Fact]
    public async Task GetMaterials_RollsOverCapMaterialTypesIntoOtherInBothTheGroupsAndTheSeries()
    {
        var typeCount = MaterialAnalyticsService.MaxGroups + 1;
        long userId;

        using (var seed = _factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<PrintLogContext>();
            var now = DateTime.UtcNow;

            var user = new PrintLogApi.Models.User
            {
                OAuthUserId = $"auth0|materials-cap-{Guid.NewGuid()}",
                ViewStatus = PrintLogApi.Models.User.ProfileViewStatus.Private,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;

            var printer = new PrintLogApi.Models.Printer
            {
                Name = "Cap Fixture Printer",
                Model = "CF1",
                Make = "Fixture",
                UserId = userId,
                IsActive = true,
            };
            db.Printers.Add(printer);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            // Descending amounts, so which types get truncated is deterministic.
            for (var i = 0; i < typeCount; i++)
            {
                var spool = new PrintLogApi.Models.Filament
                {
                    Id = Guid.NewGuid(),
                    DisplayName = $"Cap Spool {i:D2}",
                    MaterialType = $"TYPE{i:D2}",
                    ColorName = $"Colour {i:D2}",
                    ColorHex = "ff0000",
                    Brand = "Cap Brand",
                    CreatedById = userId,
                    CreatedDate = now,
                    UpdatedById = userId,
                    UpdatedDate = now,
                    DiameterMm = 1.75,
                    MaterialCategoryNickname = "filament",
                    MaterialDensityGramPerCubicCm = 1.24,
                    IsActive = true,
                    InitialNominalWeightMg = 1_000_000,
                    Source = PrintLogApi.Models.Filament.SourceMeasurement.Weight,
                };
                db.Filaments.Add(spool);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);

                db.Prints.Add(new PrintLogApi.Models.Print
                {
                    Title = $"Cap Print {i:D2}",
                    StartDate = now.AddDays(-1),
                    Status = PrintLogApi.Models.Print.PrintStatus.Success,
                    ViewStatus = PrintLogApi.Models.Print.PrintViewStatus.Private,
                    PrinterId = printer.Id,
                    CreatedById = userId,
                    CreatedDate = now,
                    UpdatedById = userId,
                    UpdatedDate = now,
                    FilamentUsage = new System.Collections.Generic.List<PrintLogApi.Models.PrintFilament>
                    {
                        new()
                        {
                            Id = Guid.NewGuid(),
                            FilamentId = spool.Id,
                            AmountMg = (typeCount - i) * 1000,
                        },
                    },
                });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
        }

        var filter = new AnalyticsFilter { TimeZone = "UTC" };
        filter.Normalize();

        using var scope = _factory.Services.CreateScope();
        var response = await scope.ServiceProvider.GetRequiredService<IMaterialAnalyticsService>()
            .GetMaterials(userId, filter, CancellationToken.None);

        // Truncated to the cap plus exactly one rollup row.
        Assert.Equal(MaterialAnalyticsService.MaxGroups + 1, response.ByType.Count);
        Assert.Equal("Other", response.ByType.Last().Label);

        // Every series key is one the UI will actually draw.
        var known = response.ByType.Select(g => g.Key).ToHashSet();
        Assert.All(response.ConsumptionOverTime, bucket =>
            Assert.All(bucket.MaterialMgByType.Keys, key => Assert.Contains(key, known)));

        // And no mass is lost on the way into the buckets — the bug this test exists for
        // dropped the over-cap types from the series entirely.
        var expected = Enumerable.Range(0, typeCount).Sum(i => (long)(typeCount - i) * 1000);
        Assert.Equal(expected, response.ByType.Sum(g => g.MaterialMg));
        Assert.Equal(
            expected,
            response.ConsumptionOverTime.Sum(b => b.MaterialMgByType.Values.Sum()));

        // The rollup is genuinely carrying the truncated types, not an empty placeholder.
        Assert.True(response.ByType.Last().MaterialMg > 0);
    }

    [Fact]
    public async Task GetMaterials_WasteCostCarriesItsOwnPricingCoverage()
    {
        var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

        // The stat tile renders its honesty note from the METRIC's coverage, so a pricing
        // exclusion recorded only at tab level would leave a partial cost unexplained.
        Assert.Equal("prints", response.WasteCost.Coverage.Population);
        Assert.True(
            response.WasteCost.Coverage.Counted <= response.WasteCost.Coverage.Total,
            "cost coverage counted more prints than it totalled");
    }

    [Fact]
    public async Task GetMaterials_CoverageCountedNeverExceedsItsOwnPopulationTotal()
    {
        var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

        // Counted was the distinct FILAMENT count while Total was the PRINT count, which
        // can report an impossible "20 of 5" for a library with more spools than prints.
        Assert.True(
            response.Coverage.Counted <= response.Coverage.Total,
            $"counted {response.Coverage.Counted} of total {response.Coverage.Total}");
    }

    [Fact]
    public async Task GetMaterials_NeverReturnsASpoolBelongingToAnotherUser()
    {
        var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        // Compared as Guids, not as strings: Guid.ToString() evaluated in SQL is the
        // provider's formatting, which is not the CLR's, and comparing those would fail for
        // a reason that has nothing to do with ownership.
        var owned = db.Filaments
            .Where(f => f.CreatedById == Mcp.McpTestData.MetricsUserId)
            .Select(f => f.Id).ToList();

        Assert.NotEmpty(response.TopSpools);
        Assert.All(response.TopSpools, s => Assert.Contains(Guid.Parse(s.FilamentId), owned));
        Assert.All(response.Runway, r => Assert.Contains(Guid.Parse(r.FilamentId), owned));
    }

    [Fact]
    public async Task GetMaterials_AnUnownedFilamentFilterYieldsEmptyRatherThanAnError()
    {
        var response = await Get(new AnalyticsFilter
        {
            TimeZone = "UTC",
            FilamentIds = { Guid.NewGuid() },
        });

        Assert.Empty(response.ByType);
        Assert.Empty(response.TopSpools);
    }
}
