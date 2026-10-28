using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Models.DTOs.Project;
using PrintLogApi.Models.SortEnums;
using PrintLogApi.Services;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// Resin totals in milliliters next to every server-summed gram total (#294). A resin print records
/// its usage as a volume (source 3); the mL figures add up only those rows, with the same
/// actual-else-estimate choice the gram totals make, and the gram figures stay exactly as they were.
///
/// <para>One user is seeded for the whole class, with three printers:</para>
/// <list type="bullet">
/// <item>Resin: two volume prints, 42.5 mL and 30.25 mL on a 1.1 g/mL bottle (80,025 mg).</item>
/// <item>Mixed: a PLA print by weight, 12,000 mg (its normalized volume must not count), and a
/// 20 mL resin print (22,000 mg).</item>
/// <item>Estimates: a print with only a 15 mL estimate; a print whose actual is a weight and whose
/// estimate is a volume (the actual wins, so 0 mL); and an unlinked 7.5 mL row (no bottle, so 0 g).</item>
/// </list>
/// </summary>
public class ResinVolumeTotalsTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private const double ResinPrinterMl = 72.75;
    private const long ResinPrinterMg = 80_025;
    private const double MixedPrinterMl = 20;
    private const long MixedPrinterMg = 34_000;
    private const double EstimatesPrinterMl = 22.5;
    private const long EstimatesPrinterMg = 21_500;
    private const double TotalMl = ResinPrinterMl + MixedPrinterMl + EstimatesPrinterMl;
    private const long TotalMg = ResinPrinterMg + MixedPrinterMg + EstimatesPrinterMg;

    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static Seeded? _seeded;

    private sealed record Seeded(
        long UserId, long ResinPrinterId, long MixedPrinterId, long EstimatesPrinterId,
        Guid BottleId, Guid SpoolId, Guid ResinProjectId, Guid MixedProjectId,
        long FirstResinPrintId, long PlaPrintId, long EstimateOnlyPrintId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PrinterAnalytics_AResinPrinter_GetsItsMlTotal_AndAnUnchangedGramTotal()
    {
        var seeded = await SeedAsync();

        var printers = await GetPrintersAsync(seeded.UserId);

        var resin = printers.Single(p => p.PrinterId == seeded.ResinPrinterId);
        Assert.Equal(ResinPrinterMl, resin.MaterialMl, 6);
        Assert.Equal(ResinPrinterMg, resin.MaterialMg);
    }

    [Fact]
    public async Task PrinterAnalytics_AMixedPrinter_GetsBoth()
    {
        var seeded = await SeedAsync();

        var printers = await GetPrintersAsync(seeded.UserId);

        var mixed = printers.Single(p => p.PrinterId == seeded.MixedPrinterId);
        Assert.Equal(MixedPrinterMl, mixed.MaterialMl, 6);
        Assert.Equal(MixedPrinterMg, mixed.MaterialMg);
    }

    [Fact]
    public async Task PrinterAnalytics_FollowsTheGramRule_ForEstimateAgainstActual()
    {
        var seeded = await SeedAsync();

        var printers = await GetPrintersAsync(seeded.UserId);

        // 15 mL from the estimate-only print, none from the print whose actual is a weight, and
        // 7.5 mL from the unlinked volume row.
        var estimates = printers.Single(p => p.PrinterId == seeded.EstimatesPrinterId);
        Assert.Equal(EstimatesPrinterMl, estimates.MaterialMl, 6);
        Assert.Equal(EstimatesPrinterMg, estimates.MaterialMg);
    }

    [Fact]
    public async Task Overview_HasAnMlTile_NextToTheUnchangedGramTile()
    {
        var seeded = await SeedAsync();
        using var scope = factory.Services.CreateScope();

        var overview = await scope.ServiceProvider.GetRequiredService<IAnalyticsService>()
            .GetOverview(seeded.UserId, Filter(), Ct);

        Assert.Equal(TotalMl, overview.Tiles.FilamentMl.Value!.Value, 6);
        Assert.Equal(TotalMg / 1000.0, overview.Tiles.FilamentGrams.Value!.Value, 6);
    }

    [Fact]
    public async Task Activity_SeriesBuckets_CarryMl()
    {
        var seeded = await SeedAsync();
        using var scope = factory.Services.CreateScope();

        var activity = await scope.ServiceProvider.GetRequiredService<IActivityAnalyticsService>()
            .GetActivity(seeded.UserId, Filter(), Ct);

        Assert.Equal(TotalMl, activity.Series.Sum(b => b.MaterialMl), 6);
        Assert.Equal(TotalMg, activity.Series.Sum(b => b.MaterialMg));
    }

    [Fact]
    public async Task ProjectSummaries_CarryMl_AndUnchangedGrams()
    {
        var seeded = await SeedAsync();
        using var scope = factory.Services.CreateScope();

        var page = await scope.ServiceProvider.GetRequiredService<IProjectService>()
            .GetProjectSummariesAsync(1, 50, seeded.UserId);

        var resin = page.Items!.Single(p => p.Id == seeded.ResinProjectId);
        Assert.Equal(ResinPrinterMl, resin.TotalFilamentVolumeMl, 6);
        Assert.Equal(ResinPrinterMg, resin.TotalFilamentWeightMg);
        var mixed = page.Items!.Single(p => p.Id == seeded.MixedProjectId);
        Assert.Equal(MixedPrinterMl, mixed.TotalFilamentVolumeMl, 6);
        Assert.Equal(MixedPrinterMg, mixed.TotalFilamentWeightMg);
    }

    [Fact]
    public async Task ProjectDetail_CarriesMl_AndUnchangedGrams()
    {
        var seeded = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var project = await scope.ServiceProvider.GetRequiredService<IProjectService>()
            .GetProjectByIdAsync(seeded.MixedProjectId);

        var detail = scope.ServiceProvider.GetRequiredService<IMapper>().Map<ProjectDetailDto>(project);

        Assert.Equal(MixedPrinterMl, detail.TotalFilamentVolumeMl, 6);
        Assert.Equal(MixedPrinterMg, detail.TotalFilamentWeightMg);
    }

    [Fact]
    public async Task GroupedFeed_ProjectRows_CarryMl_AndPrintRowsCarryTheirOwn()
    {
        var seeded = await SeedAsync();
        using var scope = factory.Services.CreateScope();

        var feed = await scope.ServiceProvider.GetRequiredService<IPrintService>()
            .GetGroupedFeedAsync(1, 100, seeded.UserId);

        var resin = feed.Items!.Single(i => i.ProjectId == seeded.ResinProjectId);
        Assert.Equal(ResinPrinterMl, resin.TotalFilamentVolumeMl!.Value, 6);
        Assert.Equal(ResinPrinterMg, resin.TotalFilamentWeightMg);
        var estimateOnly = feed.Items!.Single(i => i.Print?.Id == seeded.EstimateOnlyPrintId);
        Assert.Equal(15, estimateOnly.Print!.TotalFilamentVolumeMl, 6);
        // A print row carries its own figure on Print; the project-only field stays unset.
        Assert.Null(estimateOnly.TotalFilamentVolumeMl);
    }

    [Fact]
    public async Task PrintList_EachPrint_CarriesItsMl_AndUnchangedGrams()
    {
        var seeded = await SeedAsync();
        using var scope = factory.Services.CreateScope();

        var page = await scope.ServiceProvider.GetRequiredService<IPrintService>().SearchPrintSummary(
            new PagedRequest { PageNumber = 1, PageSize = 50 }, null,
            new SortRequest<PrintSummarySortColumn> { SortColumn = PrintSummarySortColumn.StartDate, SortDirection = SortDirection.Desc },
            null, null, null, seeded.UserId, seeded.UserId);

        var resin = page.Items!.Single(p => p.Id == seeded.FirstResinPrintId);
        Assert.Equal(42.5, resin.TotalFilamentVolumeMl, 6);
        Assert.Equal(46_750, resin.TotalFilamentWeightMg);
        var pla = page.Items!.Single(p => p.Id == seeded.PlaPrintId);
        Assert.Equal(0, pla.TotalFilamentVolumeMl);
        Assert.Equal(12_000, pla.TotalFilamentWeightMg);
    }

    [Fact]
    public async Task CsvExport_AppendsMlColumns_AfterTheExistingOnes()
    {
        var seeded = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        using var stream = new MemoryStream();

        await scope.ServiceProvider.GetRequiredService<IPrintService>()
            .WritePrintReportAsCsvForUser(seeded.UserId, stream, Ct);

        var lines = System.Text.Encoding.UTF8.GetString(stream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();
        Assert.Equal(
            "Start Date,Title,Printer Name,Printer Make,Printer Model,Estimated Print Time (s)," +
            "Estimated Filament Usage (g),Print Time (s),Filament Usage (g),Filament Type,Notes,Url," +
            "Status,View Status,Estimated Filament Usage (mL),Filament Usage (mL)",
            lines[0].TrimStart('﻿'));

        // Titles are unique per print, so each line can be found by its title.
        string[] Row(string title) => lines.Single(l => l.Contains(title, StringComparison.Ordinal)).Split(',');

        var resin = Row("Resin print 1");
        Assert.Equal("44", resin[6]); // estimated grams, unchanged
        Assert.Equal("46.75", resin[8]); // actual grams, unchanged
        Assert.Equal("40", resin[14]);
        Assert.Equal("42.5", resin[15]);

        var pla = Row("PLA print");
        Assert.Equal("12", pla[8]);
        Assert.Equal("0", pla[14]);
        Assert.Equal("0", pla[15]);

        var estimateOnly = Row("Estimate only");
        Assert.Equal("15", estimateOnly[14]);
        Assert.Equal("0", estimateOnly[15]);
    }

    [Fact]
    public async Task McpPrintSummary_CarriesMilliliters_AndUnchangedGrams()
    {
        var seeded = await SeedAsync();
        using var scope = factory.Services.CreateScope();

        var summary = await scope.ServiceProvider.GetRequiredService<IMcpStatisticsService>()
            .GetPrintSummaryForMcp(seeded.UserId, null, null, null, Ct);

        Assert.Equal(TotalMl, summary.Filtered.MaterialUsedMilliliters, 6);
        Assert.Equal(TotalMg / 1000.0, summary.Filtered.MaterialUsedGrams, 6);
        Assert.Equal(0, summary.Undated.MaterialUsedMilliliters);
    }

    [Fact]
    public async Task FilamentSummary_CarriesTheSourceMeasurement()
    {
        var seeded = await SeedAsync();
        using var scope = factory.Services.CreateScope();

        var page = await scope.ServiceProvider.GetRequiredService<IFilamentService>().GetFilamentSummaryForUser(
            seeded.UserId, SortDirection.Asc, FilamentSummarySortColumn.MaterialType, 1, 50,
            null, null, null, includeInactive: true, showFavoritesOnly: null, showLoadedFilamentOnly: null);

        Assert.Equal(Filament.SourceMeasurement.Volume, page.Items!.Single(f => f.Id == seeded.BottleId).Source);
        Assert.Equal(Filament.SourceMeasurement.Weight, page.Items!.Single(f => f.Id == seeded.SpoolId).Source);
    }

    private static AnalyticsFilter Filter()
    {
        var filter = new AnalyticsFilter { TimeZone = "UTC" };
        filter.Normalize();
        return filter;
    }

    private async Task<IReadOnlyList<PrinterRow>> GetPrintersAsync(long userId)
    {
        using var scope = factory.Services.CreateScope();
        var response = await scope.ServiceProvider.GetRequiredService<IPrinterAnalyticsService>()
            .GetPrinters(userId, Filter(), Ct);
        return response.Printers;
    }

    private async Task<Seeded> SeedAsync()
    {
        await SeedLock.WaitAsync(Ct);
        try
        {
            return _seeded ??= await SeedOnceAsync();
        }
        finally
        {
            SeedLock.Release();
        }
    }

    private async Task<Seeded> SeedOnceAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var user = new User { OAuthUserId = $"auth0|resin-totals-{Guid.NewGuid():N}", ViewStatus = User.ProfileViewStatus.Private };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);

        Printer NewPrinter(string name) => new() { Name = name, Make = "Test", Model = name, UserId = user.Id, IsActive = true };
        var resinPrinter = NewPrinter("Resin");
        var mixedPrinter = NewPrinter("Mixed");
        var estimatesPrinter = NewPrinter("Estimates");
        db.Printers.AddRange(resinPrinter, mixedPrinter, estimatesPrinter);

        var bottle = new Filament
        {
            Id = Guid.NewGuid(),
            Brand = "Test",
            DisplayName = "Grey resin",
            MaterialType = "Standard Resin",
            MaterialCategoryNickname = "resin",
            MaterialDensityGramPerCubicCm = 1.1,
            IsActive = true,
            InitialNominalWeightMg = 1_100_000,
            Source = Filament.SourceMeasurement.Volume,
            CreatedById = user.Id,
            UpdatedById = user.Id,
        };
        var spool = new Filament
        {
            Id = Guid.NewGuid(),
            Brand = "Test",
            DisplayName = "Green PLA",
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
        db.Filaments.AddRange(bottle, spool);

        Project NewProject(string name) => new() { Id = Guid.NewGuid(), Name = name, CreatedById = user.Id, UpdatedById = user.Id };
        var resinProject = NewProject("Resin project");
        var mixedProject = NewProject("Mixed project");
        db.Projects.AddRange(resinProject, mixedProject);
        await db.SaveChangesAsync(Ct);

        var day = 0;
        Print NewPrint(string title, Printer printer, Project? project, Print.PrintStatus status, params PrintFilament[] rows) => new()
        {
            Title = title,
            StartDate = DateTimeOffset.UtcNow.AddDays(-(++day)),
            Status = status,
            ViewStatus = Print.PrintViewStatus.Private,
            PrinterId = printer.Id,
            ProjectId = project?.Id,
            CreatedById = user.Id,
            UpdatedById = user.Id,
            PrintTimeInSeconds = 3_600,
            FilamentUsage = rows.ToList(),
        };

        // Rows as the API stores them: a volume row keeps the weight it derived from the bottle's
        // density, and a weight row on a spool keeps the volume it derived, which must not count.
        var resin1 = NewPrint("Resin print 1", resinPrinter, resinProject, Print.PrintStatus.Success, new PrintFilament
        {
            FilamentId = bottle.Id,
            Slot = 0,
            Source = PrintFilament.SourceMeasurement.Volume,
            VolumeMl = 42.5,
            AmountMg = 46_750,
            EstimatedSource = PrintFilament.SourceMeasurement.Volume,
            EstimatedVolumeMl = 40,
            EstimatedAmountMg = 44_000,
        });
        var resin2 = NewPrint("Resin print 2", resinPrinter, resinProject, Print.PrintStatus.Success, new PrintFilament
        {
            FilamentId = bottle.Id,
            Slot = 0,
            Source = PrintFilament.SourceMeasurement.Volume,
            VolumeMl = 30.25,
            AmountMg = 33_275,
            EstimatedSource = PrintFilament.SourceMeasurement.Volume,
        });
        var pla = NewPrint("PLA print", mixedPrinter, mixedProject, Print.PrintStatus.Success, new PrintFilament
        {
            FilamentId = spool.Id,
            Slot = 0,
            Source = PrintFilament.SourceMeasurement.Weight,
            AmountMg = 12_000,
            VolumeMl = 9.68,
            LengthInM = 4.02,
            EstimatedSource = PrintFilament.SourceMeasurement.Weight,
            EstimatedAmountMg = 11_000,
            EstimatedVolumeMl = 8.87,
        });
        var mixedResin = NewPrint("Mixed resin print", mixedPrinter, mixedProject, Print.PrintStatus.Success, new PrintFilament
        {
            FilamentId = bottle.Id,
            Slot = 0,
            Source = PrintFilament.SourceMeasurement.Volume,
            VolumeMl = 20,
            AmountMg = 22_000,
        });
        var estimateOnly = NewPrint("Estimate only", estimatesPrinter, null, Print.PrintStatus.Pending, new PrintFilament
        {
            FilamentId = bottle.Id,
            Slot = 0,
            EstimatedSource = PrintFilament.SourceMeasurement.Volume,
            EstimatedVolumeMl = 15,
            EstimatedAmountMg = 16_500,
        });
        var weightActual = NewPrint("Weight actual", estimatesPrinter, null, Print.PrintStatus.Success, new PrintFilament
        {
            FilamentId = bottle.Id,
            Slot = 0,
            Source = PrintFilament.SourceMeasurement.Weight,
            AmountMg = 5_000,
            EstimatedSource = PrintFilament.SourceMeasurement.Volume,
            EstimatedVolumeMl = 4,
            EstimatedAmountMg = 4_400,
        });
        var unlinked = NewPrint("Unlinked vat", estimatesPrinter, null, Print.PrintStatus.Success, new PrintFilament
        {
            Slot = 0,
            Source = PrintFilament.SourceMeasurement.Volume,
            VolumeMl = 7.5,
        });
        db.Prints.AddRange(resin1, resin2, pla, mixedResin, estimateOnly, weightActual, unlinked);
        await db.SaveChangesAsync(Ct);

        return new Seeded(
            user.Id, resinPrinter.Id, mixedPrinter.Id, estimatesPrinter.Id,
            bottle.Id, spool.Id, resinProject.Id, mixedProject.Id,
            resin1.Id, pla.Id, estimateOnly.Id);
    }
}
