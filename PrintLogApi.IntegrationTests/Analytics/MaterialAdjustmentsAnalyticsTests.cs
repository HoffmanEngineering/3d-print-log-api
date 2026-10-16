using Microsoft.EntityFrameworkCore;
using PrintLogApi.IntegrationTests.Services;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics;

/// <summary><see cref="MaterialAdjustmentsAnalyticsTestsBase"/> on the suite's SQLite host.</summary>
public class MaterialAdjustmentsAnalyticsTests(CustomWebApplicationFactory factory)
    : MaterialAdjustmentsAnalyticsTestsBase(factory), IClassFixture<CustomWebApplicationFactory>
{
    /// <summary>
    /// Adjustment rows alone over the row cap, with no prints at all: the print coverage has
    /// nothing to exclude, so the flag is what tells the UI the tab is empty on purpose. Here
    /// only, not on SQL Server: it writes 20,001 rows.
    /// </summary>
    [Fact]
    public async Task AdjustmentRowsOverTheCap_RaiseTheRowCapFlagWithoutAnyPrints()
    {
        long userId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = NewUser();
            db.Users.Add(user);
            await db.SaveChangesAsync(Ct);
            userId = user.Id;

            var spool = Spool(userId, "PLA", "Cap", "Red", 1_000_000);
            db.Filaments.Add(spool);
            await db.SaveChangesAsync(Ct);

            db.FilamentAdjustments.AddRange(Enumerable.Range(0, AnalyticsService.MaxSeriesRows + 1)
                .Select(_ => new FilamentAdjustment
                {
                    FilamentId = spool.Id,
                    Source = FilamentAdjustment.SourceMeasurement.Weight,
                    AmountMg = -1,
                    CreatedById = userId,
                    UpdatedById = userId,
                }));
            await db.SaveChangesAsync(Ct);
        }

        var response = await GetAsync(userId, f =>
        {
            f.FromDate = null;
            f.ToDate = null;
        });

        Assert.True(response.RowCapExceeded);
        Assert.Empty(response.ByType);
        Assert.Empty(response.ConsumptionOverTime);
    }
}

/// <summary>
/// <see cref="MaterialAdjustmentsAnalyticsTestsBase"/> against a real SQL Server, on a scratch
/// database built from the migrations and dropped afterwards. Skipped unless
/// PRINTLOG_SQLSERVER_TEST_CONNECTION is set.
/// </summary>
public class MaterialAdjustmentsAnalyticsSqlServerTests(SpoolFiguresSqlServerTests.SqlServerFactory factory)
    : MaterialAdjustmentsAnalyticsTestsBase(factory), IClassFixture<SpoolFiguresSqlServerTests.SqlServerFactory>
{
    protected override string? SkipReason =>
        Environment.GetEnvironmentVariable("PRINTLOG_SQLSERVER_TEST_CONNECTION") is null
            ? "Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run against SQL Server."
            : null;
}

/// <summary>
/// #231: filament adjustments count toward the Materials tab as their own series. An adjustment's
/// AmountMg is signed the way the spool sees it (negative removes filament), so the consumption it
/// represents is <c>-AmountMg</c>, bucketed by its CreatedDate.
///
/// <para>Each test seeds its own user, because the figures asserted are exact. CreatedDate is
/// stamped by SaveChanges, so the seed moves it afterwards with ExecuteUpdate, which bypasses that
/// hook.</para>
/// </summary>
public abstract class MaterialAdjustmentsAnalyticsTestsBase(CustomWebApplicationFactory factory)
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Set by a host that cannot run here (no SQL Server configured), to skip every scenario.</summary>
    protected virtual string? SkipReason => null;

    /// <summary>The window every scenario reads: 28 whole UTC days, ending well in the past.</summary>
    private static readonly DateTime WindowStart = DateTime.UtcNow.Date.AddDays(-60);

    private const int WindowDays = 28;

    private sealed record Seeded(long UserId, long PrinterId, Guid A, Guid B, Guid C, Guid D, Guid E);

    /// <summary>
    /// <list type="bullet">
    /// <item>A (PLA, Brand X): a 100 g print on day 2, a 30 g weigh-in loss on day 5, 10 g added back on day 10.</item>
    /// <item>B (PETG, Brand Y): no prints, a 50 g weigh-in loss on day 3.</item>
    /// <item>C (PLA, Brand X): a 70 g loss five days BEFORE the window.</item>
    /// <item>D (ABS): no prints, 20 g added back on day 7.</item>
    /// <item>E (NYLON): a zero adjustment on day 4, which says nothing.</item>
    /// <item>Another user's spool: a 999 g loss on day 5.</item>
    /// </list>
    /// The user's spools were created 30 days before the window.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var user = NewUser();
        var stranger = NewUser();
        db.Users.AddRange(user, stranger);
        await db.SaveChangesAsync(Ct);

        var printer = new Printer { Name = "Adjustments", UserId = user.Id, IsActive = true, CategoryNickname = "FDM" };
        db.Printers.Add(printer);
        await db.SaveChangesAsync(Ct);

        var a = Spool(user.Id, "PLA", "Brand X", "Red", 1_000_000, price: "25");
        var b = Spool(user.Id, "PETG", "Brand Y", "Blue", 500_000);
        var c = Spool(user.Id, "PLA", "Brand X", "Green", 1_000_000);
        var d = Spool(user.Id, "ABS", "Brand Z", "Black", 1_000_000);
        var e = Spool(user.Id, "NYLON", "Brand Z", "White", 1_000_000);
        var foreign = Spool(stranger.Id, "PLA", "Brand X", "Red", 1_000_000);
        db.Filaments.AddRange(a, b, c, d, e, foreign);
        await db.SaveChangesAsync(Ct);

        // The spools predate everything logged against them, as real ones do. SaveChanges
        // stamps CreatedDate, so it is moved afterwards.
        var spoolsCreated = WindowStart.AddDays(-30);
        await db.Filaments.Where(f => f.CreatedById == user.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(f => f.CreatedDate, spoolsCreated), Ct);

        db.Prints.Add(new Print
        {
            Title = "Adjustments print",
            StartDate = new DateTimeOffset(WindowStart.AddDays(2).AddHours(12), TimeSpan.Zero),
            Status = Print.PrintStatus.Success,
            ViewStatus = Print.PrintViewStatus.Private,
            PrinterId = printer.Id,
            CreatedById = user.Id,
            UpdatedById = user.Id,
            FilamentUsage = new List<PrintFilament>
            {
                new() { FilamentId = a.Id, AmountMg = 100_000, Source = PrintFilament.SourceMeasurement.Weight },
            },
        });
        await db.SaveChangesAsync(Ct);

        await AddAdjustmentAsync(db, user.Id, a.Id, -30_000, day: 5);
        await AddAdjustmentAsync(db, user.Id, a.Id, 10_000, day: 10);
        await AddAdjustmentAsync(db, user.Id, b.Id, -50_000, day: 3);
        await AddAdjustmentAsync(db, user.Id, c.Id, -70_000, day: -5);
        await AddAdjustmentAsync(db, user.Id, d.Id, 20_000, day: 7);
        await AddAdjustmentAsync(db, user.Id, e.Id, 0, day: 4);
        await AddAdjustmentAsync(db, stranger.Id, foreign.Id, -999_000, day: 5);

        return new Seeded(user.Id, printer.Id, a.Id, b.Id, c.Id, d.Id, e.Id);
    }

    /// <summary>
    /// An adjustment whose CreatedDate is <see cref="DateTime.MinValue"/>, as #256 leaves rows a
    /// filament-detail save re-sent without one. <paramref name="updatedDate"/> is what that
    /// save's UpdatedDate would hold, or MinValue too when the row is beyond recovery.
    /// </summary>
    private static async Task AddUndatedAdjustmentAsync(
        PrintLogContext db, long userId, Guid filamentId, long amountMg, DateTime updatedDate)
    {
        var adjustment = new FilamentAdjustment
        {
            FilamentId = filamentId,
            Source = FilamentAdjustment.SourceMeasurement.Weight,
            AmountMg = amountMg,
            CreatedById = userId,
            UpdatedById = userId,
        };
        db.FilamentAdjustments.Add(adjustment);
        await db.SaveChangesAsync(Ct);

        await db.FilamentAdjustments.Where(x => x.Id == adjustment.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CreatedDate, DateTime.MinValue)
                .SetProperty(x => x.UpdatedDate, updatedDate), Ct);
    }

    private static async Task AddAdjustmentAsync(PrintLogContext db, long userId, Guid filamentId, long amountMg, int day)
    {
        var adjustment = new FilamentAdjustment
        {
            FilamentId = filamentId,
            Source = FilamentAdjustment.SourceMeasurement.Weight,
            AmountMg = amountMg,
            CreatedById = userId,
            UpdatedById = userId,
        };
        db.FilamentAdjustments.Add(adjustment);
        await db.SaveChangesAsync(Ct);

        var created = WindowStart.AddDays(day).AddHours(12);
        await db.FilamentAdjustments.Where(x => x.Id == adjustment.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedDate, created), Ct);
    }

    protected static User NewUser() => new()
    {
        OAuthUserId = $"auth0|adjustments-{Guid.NewGuid():N}",
        ViewStatus = User.ProfileViewStatus.Private,
    };

    protected static Filament Spool(long userId, string type, string brand, string color, long initialMg, string? price = null) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = $"{brand} {type} {color}",
        MaterialType = type,
        Brand = brand,
        ColorName = color,
        ColorHex = "ff0000",
        CreatedById = userId,
        UpdatedById = userId,
        DiameterMm = 1.75,
        MaterialCategoryNickname = "filament",
        MaterialDensityGramPerCubicCm = 1.24,
        IsActive = true,
        InitialNominalWeightMg = initialMg,
        PurchasePriceValue = price,
        Source = Filament.SourceMeasurement.Weight,
    };

    protected async Task<MaterialsResponse> GetAsync(long userId, Action<AnalyticsFilter>? configure = null)
    {
        var filter = new AnalyticsFilter
        {
            TimeZone = "UTC",
            FromDate = new DateTimeOffset(WindowStart, TimeSpan.Zero),
            ToDate = new DateTimeOffset(WindowStart.AddDays(WindowDays), TimeSpan.Zero),
        };
        configure?.Invoke(filter);
        filter.Normalize();

        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMaterialAnalyticsService>()
            .GetMaterials(userId, filter, Ct);
    }

    private static DateOnly Day(int offset) => DateOnly.FromDateTime(WindowStart.AddDays(offset));

    [Fact]
    public async Task Series_CarriesAdjustmentsAsTheirOwnSignedFigure_BesidePrintConsumption()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
        var seeded = await SeedAsync();

        var response = await GetAsync(seeded.UserId);

        Assert.Equal(WindowDays, response.ConsumptionOverTime.Count);
        var byDay = response.ConsumptionOverTime.ToDictionary(b => b.LocalStart);

        // Prints stay in MaterialMgByType, untouched by adjustments.
        Assert.Equal(100_000, byDay[Day(2)].MaterialMgByType["PLA"]);
        Assert.Equal(0, byDay[Day(2)].AdjustmentMg);

        // Removed filament is consumption; filament added back is negative consumption.
        Assert.Equal(50_000, byDay[Day(3)].AdjustmentMg);
        Assert.Equal(30_000, byDay[Day(5)].AdjustmentMg);
        Assert.Equal(-20_000, byDay[Day(7)].AdjustmentMg);
        Assert.Equal(-10_000, byDay[Day(10)].AdjustmentMg);
        Assert.Empty(byDay[Day(5)].MaterialMgByType);

        // Nothing outside the window, nothing of another user's, and the zero row says nothing.
        Assert.Equal(50_000, response.ConsumptionOverTime.Sum(b => b.AdjustmentMg));
        Assert.Equal(100_000, response.ConsumptionOverTime.Sum(b => b.MaterialMgByType.Values.Sum()));
    }

    [Fact]
    public async Task Groups_AttributeAdjustmentsToTheirSpoolsTypeBrandAndColor()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
        var seeded = await SeedAsync();

        var response = await GetAsync(seeded.UserId);

        // Ranked by prints plus adjustments; MaterialMg and PrintCount stay print-only. ABS
        // (D) had filament added back and nothing printed: no mass, so no group, the same
        // rule that drops a usage row with no amount.
        Assert.Equal(new[] { "PLA", "PETG" }, response.ByType.Select(g => g.Key));
        var pla = response.ByType[0];
        Assert.Equal((1, 100_000L, 20_000L), (pla.PrintCount, pla.MaterialMg, pla.AdjustmentMg));
        var petg = response.ByType[1];
        Assert.Equal((0, 0L, 50_000L), (petg.PrintCount, petg.MaterialMg, petg.AdjustmentMg));

        Assert.Equal(new[] { "Brand X", "Brand Y" }, response.ByBrand.Select(g => g.Key));
        Assert.Equal(new[] { "Red", "Blue" }, response.ByColor.Select(g => g.Key));

        // Every view accounts for the same adjustment mass: the series' 50 g less D's
        // dropped -20 g.
        foreach (var groups in new[] { response.ByType, response.ByBrand, response.ByColor })
        {
            Assert.Equal(70_000, groups.Sum(g => g.AdjustmentMg));
        }
    }

    [Fact]
    public async Task TopSpools_CountAdjustmentsInPercentAndCostConsumed()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
        var seeded = await SeedAsync();

        var response = await GetAsync(seeded.UserId);

        // D only had filament added back, so nothing was consumed from it in the window.
        Assert.Equal(new[] { seeded.A, seeded.B }, response.TopSpools.Select(s => Guid.Parse(s.FilamentId)));

        var a = response.TopSpools[0];
        Assert.Equal(100_000, a.UsedMg);
        Assert.Equal(20_000, a.AdjustmentMg);
        Assert.Equal(12.0, a.PercentConsumed!.Value, 6);
        // 25 per kg, 120 g consumed.
        Assert.Equal(3.00m, a.CostConsumed);
        // Remaining is the spool's lifetime figure, as everywhere else.
        Assert.Equal(1_000_000 - 100_000 - 30_000 + 10_000, a.RemainingMg);

        var b = response.TopSpools[1];
        Assert.Equal(0, b.UsedMg);
        Assert.Equal(50_000, b.AdjustmentMg);
        Assert.Equal(10.0, b.PercentConsumed!.Value, 6);
    }

    [Fact]
    public async Task Runway_BurnsPrintsPlusAdjustments_AndFloorsANetGainAtZero()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
        var seeded = await SeedAsync();

        // A spool whose window is a small print and a larger add-back: a net gain.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var gain = Spool(seeded.UserId, "TPU", "Brand Q", "Clear", 1_000_000);
            db.Filaments.Add(gain);
            await db.SaveChangesAsync(Ct);
            db.Prints.Add(new Print
            {
                Title = "Small print",
                StartDate = new DateTimeOffset(WindowStart.AddDays(1), TimeSpan.Zero),
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
                PrinterId = seeded.PrinterId,
                CreatedById = seeded.UserId,
                UpdatedById = seeded.UserId,
                FilamentUsage = new List<PrintFilament>
                {
                    new() { FilamentId = gain.Id, AmountMg = 10_000, Source = PrintFilament.SourceMeasurement.Weight },
                },
            });
            await db.SaveChangesAsync(Ct);
            await AddAdjustmentAsync(db, seeded.UserId, gain.Id, 40_000, day: 6);

            var response = await GetAsync(seeded.UserId);

            var runway = response.Runway.ToDictionary(r => Guid.Parse(r.FilamentId));
            Assert.Equal(120.0 / WindowDays, runway[seeded.A].BurnRateGramsPerDay, 6);
            Assert.Equal(50.0 / WindowDays, runway[seeded.B].BurnRateGramsPerDay, 6);
            Assert.Equal(450.0 / (50.0 / WindowDays), runway[seeded.B].RunwayDays!.Value, 6);

            // Net -30 g over the window: no burn, so no runway, and no negative percentage.
            Assert.Equal(0, runway[gain.Id].BurnRateGramsPerDay);
            Assert.Null(runway[gain.Id].RunwayDays);
            var gainSpool = response.TopSpools.Single(s => Guid.Parse(s.FilamentId) == gain.Id);
            Assert.Equal(-40_000, gainSpool.AdjustmentMg);
            Assert.Equal(0.0, gainSpool.PercentConsumed);

            // A group with prints stays, even when its adjustments outweigh them.
            var tpu = response.ByType.Single(g => g.Key == "TPU");
            Assert.Equal((1, 10_000L, -40_000L), (tpu.PrintCount, tpu.MaterialMg, tpu.AdjustmentMg));
        }
    }

    [Fact]
    public async Task APrintOnlyFilter_LeavesAdjustmentsOut()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
        var seeded = await SeedAsync();

        // An adjustment has no printer, project or status, so a filter on any of them cannot
        // claim it.
        var byPrinter = await GetAsync(seeded.UserId, f => f.PrinterIds.Add(seeded.PrinterId));
        var byStatus = await GetAsync(seeded.UserId, f => f.Statuses.Add(Print.PrintStatus.Success));

        foreach (var response in new[] { byPrinter, byStatus })
        {
            Assert.Equal(new[] { "PLA" }, response.ByType.Select(g => g.Key));
            Assert.Equal(0, response.ByType[0].AdjustmentMg);
            Assert.All(response.ConsumptionOverTime, b => Assert.Equal(0, b.AdjustmentMg));
            Assert.Equal(new[] { seeded.A }, response.TopSpools.Select(s => Guid.Parse(s.FilamentId)));
        }
    }

    [Fact]
    public async Task ASpoolFilter_KeepsOnlyThatSpoolsAdjustments()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
        var seeded = await SeedAsync();

        var onlyB = await GetAsync(seeded.UserId, f => f.FilamentIds.Add(seeded.B));
        Assert.Equal(new[] { "PETG" }, onlyB.ByType.Select(g => g.Key));
        Assert.Equal(50_000, onlyB.ConsumptionOverTime.Sum(b => b.AdjustmentMg));
        Assert.Equal(new[] { seeded.B }, onlyB.TopSpools.Select(s => Guid.Parse(s.FilamentId)));

        var onlyA = await GetAsync(seeded.UserId, f => f.FilamentIds.Add(seeded.A));
        Assert.Equal(20_000, onlyA.ConsumptionOverTime.Sum(b => b.AdjustmentMg));
        Assert.Equal(new[] { "PLA" }, onlyA.ByType.Select(g => g.Key));
    }

    [Fact]
    public async Task AllTime_StartsTheSeriesAtTheEarliestAdjustment()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
        var seeded = await SeedAsync();

        var response = await GetAsync(seeded.UserId, f =>
        {
            f.FromDate = null;
            f.ToDate = null;
        });

        // C's loss, five days before the window, is the earliest dated activity, so all-time
        // includes it rather than starting at the first print.
        Assert.Equal(50_000 + 70_000, response.ConsumptionOverTime.Sum(b => b.AdjustmentMg));
        Assert.Contains(response.TopSpools, s => Guid.Parse(s.FilamentId) == seeded.C);
    }

    /// <summary>
    /// #256 leaves adjustments with CreatedDate = 0001-01-01. Taken at face value, an all-time
    /// series started there: east of UTC the first bucket's local midnight is before
    /// DateTime.MinValue and the request threw, and west of UTC it built about 24,000 monthly
    /// buckets. Such a row is dated by its UpdatedDate instead, in the window filter and the
    /// bucketing alike.
    /// </summary>
    [Theory]
    [InlineData("UTC")]
    [InlineData("Australia/Sydney")]
    [InlineData("America/Los_Angeles")]
    public async Task AnAdjustmentWithNoCreatedDate_IsDatedByItsUpdatedDate(string timeZone)
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
        var seeded = await SeedAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await AddUndatedAdjustmentAsync(db, seeded.UserId, seeded.A, -5_000, WindowStart.AddDays(8).AddHours(12));
        }

        var allTime = await GetAsync(seeded.UserId, f =>
        {
            f.FromDate = null;
            f.ToDate = null;
            f.TimeZone = timeZone;
        });

        // From C's loss 65 days ago to now, by month: a handful of buckets, not thousands.
        Assert.InRange(allTime.ConsumptionOverTime.Count, 3, 5);
        Assert.True(allTime.ConsumptionOverTime[0].LocalStart.Year >= 2000);
        Assert.Equal(50_000 + 70_000 + 5_000, allTime.ConsumptionOverTime.Sum(b => b.AdjustmentMg));

        // And the ranged view counts it on that day rather than dropping it.
        var ranged = await GetAsync(seeded.UserId, f => f.TimeZone = timeZone);
        Assert.Equal(55_000, ranged.ConsumptionOverTime.Sum(b => b.AdjustmentMg));
        if (timeZone == "UTC")
        {
            Assert.Equal(5_000, ranged.ConsumptionOverTime.Single(b => b.LocalStart == Day(8)).AdjustmentMg);
        }
    }

    /// <summary>
    /// When UpdatedDate is no better, the row falls back to its spool's CreatedDate: the
    /// earliest the adjustment can have been made, and still a real date. B was created 30
    /// days before the window.
    /// </summary>
    [Fact]
    public async Task AnAdjustmentWithNoUsableDate_IsDatedByItsSpoolsCreation()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
        var seeded = await SeedAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await AddUndatedAdjustmentAsync(db, seeded.UserId, seeded.B, -7_000, DateTime.MinValue);
        }

        var ranged = await GetAsync(seeded.UserId, f => f.TimeZone = "Australia/Sydney");
        Assert.Equal(50_000, ranged.ConsumptionOverTime.Sum(b => b.AdjustmentMg));

        var allTime = await GetAsync(seeded.UserId, f =>
        {
            f.FromDate = null;
            f.ToDate = null;
            f.TimeZone = "Australia/Sydney";
            f.Granularity = AnalyticsGranularity.Day;
        });

        // Daily from the spool's creation, 90 days ago, to today: the row starts the series.
        Assert.InRange(allTime.ConsumptionOverTime.Count, 89, 92);
        Assert.Equal(Day(-30), allTime.ConsumptionOverTime[0].LocalStart);
        Assert.Equal(7_000, allTime.ConsumptionOverTime[0].AdjustmentMg);
        Assert.Equal(127_000, allTime.ConsumptionOverTime.Sum(b => b.AdjustmentMg));
    }

    /// <summary>
    /// Coverage counts prints, so a window holding only adjustments reported "0 of 0" and a
    /// row-cap overflow there excluded 0 prints, which drops the notice. The response says
    /// how many adjustments it counted, and whether the cap bit, on its own.
    /// </summary>
    [Fact]
    public async Task AdjustmentsOnly_AreCountedOutsideThePrintCoverage()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
        var seeded = await SeedAsync();

        var onlyB = await GetAsync(seeded.UserId, f => f.FilamentIds.Add(seeded.B));
        Assert.Equal(0, onlyB.Coverage.Total);
        Assert.Equal(1, onlyB.AdjustmentCount);
        Assert.False(onlyB.RowCapExceeded);

        // A, B, D in the window; C is outside it, E is a zero and another user's is not ours.
        var everything = await GetAsync(seeded.UserId);
        Assert.Equal(4, everything.AdjustmentCount);
    }
}
