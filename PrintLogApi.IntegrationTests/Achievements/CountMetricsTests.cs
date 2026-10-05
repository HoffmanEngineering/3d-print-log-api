using PrintLogApi.Models;
using Xunit;
using static PrintLogApi.IntegrationTests.Achievements.AchievementMetricTestKit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Achievements;

public class CountMetricsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CountMetricsTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(IServiceScope Scope, PrintLogContext Db, User User)> ArrangeAsync()
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return (scope, db, await AchievementTestData.CreateUserAsync(db));
    }

    private async Task<long> MeasureAsync(IServiceScope scope, PrintLogContext db, User user, string key)
    {
        var value = await Context(scope.ServiceProvider, db, user.Id).MeasureAsync(key, Ct);
        Assert.Equal(value.Best, value.Current);
        return value.Best;
    }

    [Fact]
    public async Task PrintsCount_CountsAllPrints()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        await AchievementTestData.AddPrintAsync(db, user);
        await AchievementTestData.AddPrintAsync(db, user, p => p.Status = PrintStatus.Failed);

        Assert.Equal(2, await MeasureAsync(scope, db, user, "prints.count"));
    }

    [Fact]
    public async Task Hours_UseActualThenEstimate_SuccessAndPartialOnly()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        await AchievementTestData.AddPrintAsync(db, user, p => p.PrintTimeInSeconds = 3600 * 10);
        await AchievementTestData.AddPrintAsync(db, user, p => { p.Status = PrintStatus.PartialSuccess; p.EstimatedPrintTimeInSeconds = 3600 * 5; });
        // A stored zero is "not recorded", so the estimate is used (PrintMetrics rule).
        await AchievementTestData.AddPrintAsync(db, user, p => { p.PrintTimeInSeconds = 0; p.EstimatedPrintTimeInSeconds = 3600 * 2; });
        await AchievementTestData.AddPrintAsync(db, user, p => { p.Status = PrintStatus.Failed; p.PrintTimeInSeconds = 3600 * 50; });
        await AchievementTestData.AddPrintAsync(db, user, p => { p.Status = PrintStatus.Cancelled; p.PrintTimeInSeconds = 3600 * 50; });

        Assert.Equal(17, await MeasureAsync(scope, db, user, "prints.hours"));
    }

    [Fact]
    public async Task MaterialKg_UsesCanonicalMaterialRule()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        var spool = await AddFilamentAsync(db, user);
        // Rows fall back to their own estimate; the scalar is "other filament" and adds
        // (PrintMetrics.MaterialMgExpr): 600 g + 500 g + 400 g = 1.5 kg.
        var print = await AchievementTestData.AddPrintAsync(db, user, p => p.FilamentUsageMg = 400_000);
        await AddUsageAsync(db, print, spool.Id, amountMg: 600_000);
        await AddUsageAsync(db, print, spool.Id, amountMg: null, estimatedMg: 500_000);
        // Failed prints never count toward material.
        var failed = await AchievementTestData.AddPrintAsync(db, user, p => { p.Status = PrintStatus.Failed; p.FilamentUsageMg = 9_000_000; });
        await AddUsageAsync(db, failed, spool.Id, amountMg: 9_000_000);

        Assert.Equal(1, await MeasureAsync(scope, db, user, "prints.materialKg"));
    }

    [Fact]
    public async Task LongestHours_SuccessOnly()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        await AchievementTestData.AddPrintAsync(db, user, p => p.PrintTimeInSeconds = 3600 * 9);
        await AchievementTestData.AddPrintAsync(db, user, p => { p.Status = PrintStatus.PartialSuccess; p.PrintTimeInSeconds = 3600 * 30; });

        Assert.Equal(9, await MeasureAsync(scope, db, user, "prints.longestHours"));
    }

    [Fact]
    public async Task WithImage_Public_Failed_AreCounts()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        var withImage = await AchievementTestData.AddPrintAsync(db, user, p => p.ViewStatus = PrintViewStatus.Public);
        await AddImageAsync(db, withImage, user.Id);
        await AddImageAsync(db, withImage, user.Id);
        await AchievementTestData.AddPrintAsync(db, user, p => p.Status = PrintStatus.Failed);
        await AchievementTestData.AddPrintAsync(db, user, p => p.ViewStatus = PrintViewStatus.Unlisted);

        Assert.Equal(1, await MeasureAsync(scope, db, user, "prints.withImage"));
        Assert.Equal(1, await MeasureAsync(scope, db, user, "prints.public"));
        Assert.Equal(1, await MeasureAsync(scope, db, user, "prints.failed"));
    }

    [Fact]
    public async Task SourceCounts_BySource()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        await AchievementTestData.AddPrintAsync(db, user, p => { p.Source = PrintSource.SlicerPlugin; p.Slicer = "cura"; });
        await AchievementTestData.AddPrintAsync(db, user, p => p.Source = PrintSource.OctoPrint);
        await AchievementTestData.AddPrintAsync(db, user, p => p.Source = PrintSource.Moonraker);
        await AchievementTestData.AddPrintAsync(db, user, p => p.Source = PrintSource.Mcp);
        await AchievementTestData.AddPrintAsync(db, user, p => p.Source = PrintSource.Web);

        Assert.Equal(1, await MeasureAsync(scope, db, user, "prints.slicerPlugin"));
        Assert.Equal(1, await MeasureAsync(scope, db, user, "source.octoprint"));
        Assert.Equal(1, await MeasureAsync(scope, db, user, "source.moonraker"));
        Assert.Equal(1, await MeasureAsync(scope, db, user, "source.mcp"));
    }

    [Fact]
    public async Task SlicerKeys_AreOneWhenUsedThroughThePlugin()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        await AchievementTestData.AddPrintAsync(db, user, p => { p.Source = PrintSource.SlicerPlugin; p.Slicer = "orcaslicer"; });
        // A slicer recorded on a non-plugin print does not count.
        await AchievementTestData.AddPrintAsync(db, user, p => { p.Source = PrintSource.Web; p.Slicer = "prusaslicer"; });

        Assert.Equal(1, await MeasureAsync(scope, db, user, "slicer.orcaslicer"));
        Assert.Equal(0, await MeasureAsync(scope, db, user, "slicer.prusaslicer"));
        Assert.Equal(0, await MeasureAsync(scope, db, user, "slicer.cura"));
    }

    [Fact]
    public async Task SlicerDistinct_CountsFlsunButNotOther()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        foreach (var slicer in new[] { "cura", "cura", "flsun", "other" })
        {
            await AchievementTestData.AddPrintAsync(db, user, p => { p.Source = PrintSource.SlicerPlugin; p.Slicer = slicer; });
        }

        Assert.Equal(2, await MeasureAsync(scope, db, user, "slicer.distinct"));
    }

    [Fact]
    public async Task MaxDistinctMaterials_IgnoresDuplicateAndNullFilamentIds()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        var a = await AddFilamentAsync(db, user);
        var b = await AddFilamentAsync(db, user);
        var print = await AchievementTestData.AddPrintAsync(db, user);
        await AddUsageAsync(db, print, a.Id);
        await AddUsageAsync(db, print, a.Id);
        await AddUsageAsync(db, print, null);
        await AddUsageAsync(db, print, b.Id);
        var single = await AchievementTestData.AddPrintAsync(db, user);
        await AddUsageAsync(db, single, a.Id);

        Assert.Equal(2, await MeasureAsync(scope, db, user, "prints.maxDistinctMaterials"));
    }

    [Fact]
    public async Task MaterialTypes_DistinctNonEmptyAcrossUsage()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        var pla = await AddFilamentAsync(db, user, "PLA");
        var pla2 = await AddFilamentAsync(db, user, "PLA");
        var petg = await AddFilamentAsync(db, user, "PETG");
        var blank = await AddFilamentAsync(db, user, "");
        await AddFilamentAsync(db, user, "ABS"); // owned but never printed with
        var print = await AchievementTestData.AddPrintAsync(db, user);
        foreach (var f in new[] { pla, pla2, petg, blank })
        {
            await AddUsageAsync(db, print, f.Id);
        }

        Assert.Equal(2, await MeasureAsync(scope, db, user, "prints.materialTypes"));
    }

    [Fact]
    public async Task EntityCounts_PrintersFilamentsProjectsMaintenance()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        var printer = await AchievementTestData.AddPrinterAsync(db, user);
        await AchievementTestData.AddPrinterAsync(db, user);
        await AddFilamentAsync(db, user);
        db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "P", CreatedById = user.Id, UpdatedById = user.Id });
        db.PrinterMaintenance.Add(new PrinterMaintenance { PrinterId = printer.Id, Date = DateTimeOffset.UtcNow, CreatedById = user.Id, UpdatedById = user.Id });
        db.PrinterMaintenance.Add(new PrinterMaintenance { PrinterId = printer.Id, Date = DateTimeOffset.UtcNow, CreatedById = user.Id, UpdatedById = user.Id });
        await db.SaveChangesAsync(Ct);

        Assert.Equal(2, await MeasureAsync(scope, db, user, "printers.count"));
        Assert.Equal(1, await MeasureAsync(scope, db, user, "filaments.count"));
        Assert.Equal(1, await MeasureAsync(scope, db, user, "projects.count"));
        Assert.Equal(2, await MeasureAsync(scope, db, user, "maintenance.count"));
    }

    [Fact]
    public async Task CommentsReceived_ExcludesSelfAndCountsDistinct()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        var x = await AchievementTestData.CreateUserAsync(db);
        var y = await AchievementTestData.CreateUserAsync(db);
        var print = await AchievementTestData.AddPrintAsync(db, user);
        for (var i = 0; i < 3; i++)
        {
            await AddCommentAsync(db, print, x.Id);
        }
        await AddCommentAsync(db, print, y.Id);
        await AddCommentAsync(db, print, user.Id);
        await AddCommentAsync(db, print, user.Id);

        Assert.Equal(2, await MeasureAsync(scope, db, user, "comments.distinctCommenters"));
    }

    [Fact]
    public async Task ProfileComplete_RequiresAllThreeFields()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        user.DisplayName = "Maker";
        user.ProfilePicture = "https://example.test/me.png";
        user.Bio = "   ";
        await db.SaveChangesAsync(Ct);
        Assert.Equal(0, await MeasureAsync(scope, db, user, "profile.complete"));

        user.Bio = "I print things.";
        await db.SaveChangesAsync(Ct);
        Assert.Equal(1, await MeasureAsync(scope, db, user, "profile.complete"));
    }

    [Fact]
    public async Task UnknownMetric_Throws()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;

        await Assert.ThrowsAsync<KeyNotFoundException>(() => Context(scope.ServiceProvider, db, user.Id).MeasureAsync("nope", Ct));
    }

    [Fact]
    public async Task PrintAggregate_AtMostThreeCommands_DatesReuseTheRows()
    {
        var (scope, seedDb, user) = await ArrangeAsync();
        using var _ = scope;
        var spool = await AddFilamentAsync(seedDb, user);
        var print = await AchievementTestData.AddPrintAsync(seedDb, user, p => { p.Source = PrintSource.SlicerPlugin; p.Slicer = "cura"; });
        await AddUsageAsync(seedDb, print, spool.Id);

        var (db, counter) = CountingContext(scope.ServiceProvider);
        await using var __ = db;
        var ctx = Context(scope.ServiceProvider, db, user.Id);

        var aggregate = await ctx.GetPrintAggregateAsync(Ct);
        await ctx.GetPrintAggregateAsync(Ct); // memoized
        Assert.Equal(1, aggregate.PrintCount);
        Assert.Equal(["cura"], aggregate.PluginSlicers);
        // Print rows, usage rows, photos.
        Assert.Equal(3, counter.Commands.Count);

        await ctx.GetPrintDatesAsync(Ct);
        Assert.Equal(3, counter.Commands.Count);
    }

    [Fact]
    public async Task PrintMetrics_TotalBudgetIsThree()
    {
        var (scope, seedDb, user) = await ArrangeAsync();
        using var _ = scope;
        await AchievementTestData.AddPrintAsync(seedDb, user);

        var (db, counter) = CountingContext(scope.ServiceProvider);
        await using var __ = db;
        var ctx = Context(scope.ServiceProvider, db, user.Id);

        string[] printMetrics =
        [
            "prints.count", "prints.hours", "prints.materialKg", "prints.longestHours", "prints.withImage",
            "prints.public", "prints.failed", "prints.slicerPlugin", "source.octoprint", "source.moonraker",
            "source.mcp", "slicer.cura", "slicer.prusaslicer", "slicer.orcaslicer", "slicer.bambustudio",
            "slicer.anycubic", "slicer.distinct", "prints.maxDistinctMaterials", "prints.materialTypes",
        ];
        foreach (var key in printMetrics)
        {
            await ctx.MeasureAsync(key, Ct);
        }
        Assert.InRange(counter.Commands.Count, 1, 3);

        // Every other entity costs one command each.
        var before = counter.Commands.Count;
        foreach (var key in new[] { "printers.count", "filaments.count", "projects.count", "maintenance.count", "comments.distinctCommenters", "profile.complete" })
        {
            await ctx.MeasureAsync(key, Ct);
        }
        Assert.Equal(before + 6, counter.Commands.Count);
    }
}
