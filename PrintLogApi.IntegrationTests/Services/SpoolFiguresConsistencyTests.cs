using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Achievements.Metrics;
using PrintLogApi.IntegrationTests.Spoolman;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Services;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// <see cref="SpoolFiguresConsistencyTestsBase"/> on the suite's SQLite host.
/// </summary>
public class SpoolFiguresConsistencyTests(CustomWebApplicationFactory factory)
    : SpoolFiguresConsistencyTestsBase(factory), IClassFixture<CustomWebApplicationFactory>;

/// <summary>
/// One spool, every kind of usage row and adjustment, and every place the product reports how
/// much is left on it. They must all agree, because they are all supposed to be the one rule in
/// <c>FilamentProfile</c>. Each surface used to carry its own copy of that rule, and two had
/// drifted: <c>get_printer</c> ignored usage recorded only as a length or volume, and the
/// Spoolman API counted only prints as used, so a weigh-in lowered <c>remaining_weight</c>
/// without raising <c>used_weight</c>.
///
/// <para>The per-print half (#229) asks the same of every place that totals a print's usage:
/// overview, printer and activity analytics, achievements, the MCP print tools, the print list,
/// stats and CSV export, projects and the grouped feed. Those resolved a row as weight, then
/// estimated weight, so a row recorded only as a length or volume counted as nothing there while
/// the spool's remaining weight had already subtracted it.</para>
///
/// <para>The usage rows are written directly rather than through PrintService on purpose:
/// PrintService fills every measure in, which would hide a surface that reads only AmountMg.</para>
///
/// <para>Abstract so the same scenarios run on SQL Server too (<c>SpoolFiguresSqlServerTests</c>):
/// SQLite accepts aggregates SQL Server refuses (error 8124), and these figures are exactly the
/// correlated sums that trip it.</para>
/// </summary>
public abstract class SpoolFiguresConsistencyTestsBase(CustomWebApplicationFactory factory)
{
    private const double Density = 1.24;
    private const double DiameterMm = 1.75;
    private const long InitialMg = 1_000_000;

    private readonly HttpClient _client = factory.CreateClient();
    private readonly SpoolmanTestSupport _support = new(factory.Services);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Set by a host that cannot run here (no SQL Server configured), to skip every scenario.</summary>
    protected virtual string? SkipReason => null;

    private static double LengthMg(double meters) => 250.0 * Math.PI * Density * DiameterMm * DiameterMm * meters;

    private static double VolumeMg(double ml) => ml * Density * 1000.0;

    [Fact]
    public async Task EverySurface_ReportsTheSameRemaining_CountingPrintsAndAdjustments()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");

        var user = await _support.CreateUserAsync();
        var printerId = await _support.CreatePrinterAsync(user);
        var connection = await _support.ConnectAsync(user, printerId);
        var spool = await AddSpoolAsync(user);

        const double lengthOnlyM = 10;
        const double volumeOnlyMl = 5;
        var printsMg = 100_000                                                      // actual weight
            + LengthMg(lengthOnlyM)                                                 // actual length only
            + VolumeMg(volumeOnlyMl)                                                // actual volume only
            + 40_000;                                                               // estimate only
        const long netAdjustmentMg = -150_000 + 20_000;                             // a weigh-in, then a correction back up
        var consumedMg = (long)Math.Round(printsMg - netAdjustmentMg);
        var remainingMg = (long)Math.Round(InitialMg - (printsMg - netAdjustmentMg));

        await WithDbAsync(async db =>
        {
            var print = NewPrint(user, printerId, "Mixed usage");
            db.Prints.Add(print);
            await db.SaveChangesAsync(Ct);

            db.PrintFilament.AddRange(
                Usage(print.Id, spool.Id, r => r.AmountMg = 100_000),
                Usage(print.Id, spool.Id, r => r.LengthInM = lengthOnlyM),
                Usage(print.Id, spool.Id, r => r.VolumeMl = volumeOnlyMl),
                Usage(print.Id, spool.Id, r => r.EstimatedAmountMg = 40_000));
            db.FilamentAdjustments.AddRange(
                Adjustment(user, spool.Id, -150_000),
                Adjustment(user, spool.Id, 20_000));
            db.PrinterFilament.Add(new PrinterFilament
            {
                PrinterId = printerId,
                FilamentId = spool.Id,
                LoadedDateTime = DateTimeOffset.UtcNow,
                Slot = 0,
            });
            await db.SaveChangesAsync(Ct);
        });
        factory.Services.GetRequiredService<ICacheVersionService>().InvalidateUserCache(user.Id);

        // REST: the detail page, the list, and the printer page's loaded spool (mapped in memory).
        var detail = await RestAsync<FilamentDetailDto>(user, $"/api/Filaments/{spool.Id}");
        Assert.Equal(remainingMg, detail.FilamentRemaining);

        var list = await RestJsonAsync(user, "/api/Filaments");
        var listed = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == spool.Id);
        Assert.Equal(remainingMg, listed.GetProperty("filamentRemaining").GetInt64());

        var printer = await RestJsonAsync(user, $"/api/Printers/{printerId}");
        var loadedRest = printer.GetProperty("loadedFilaments").EnumerateArray().Single();
        Assert.Equal(remainingMg, loadedRest.GetProperty("filament").GetProperty("filamentRemaining").GetInt64());

        // MCP: get_material_inventory, get_material, get_printer.
        using (var scope = factory.Services.CreateScope())
        {
            var filaments = scope.ServiceProvider.GetRequiredService<IFilamentService>();
            var inventory = await filaments.GetMaterialInventoryForMcp(user.Id, 1, 25, null, null, true, Ct);
            Assert.Equal(remainingMg / 1000.0, inventory.Items.Single().RemainingGrams, 3);

            var material = await filaments.GetOwnMaterialDetailForMcp(user.Id, spool.Id, Ct);
            Assert.Equal(remainingMg / 1000.0, material.RemainingGrams, 3);

            var mcpPrinter = await scope.ServiceProvider.GetRequiredService<IPrinterService>().GetPrinterForMcp(user.Id, printerId, Ct);
            Assert.Equal(remainingMg / 1000.0, mcpPrinter.LoadedFilaments.Single().RemainingGrams, 3);

            // Analytics: the Materials tab's top spools.
            var materials = await scope.ServiceProvider.GetRequiredService<IMaterialAnalyticsService>()
                .GetMaterials(user.Id, new AnalyticsFilter { TimeZone = "UTC" }, Ct);
            Assert.Equal(remainingMg, materials.TopSpools.Single().RemainingMg);
        }

        // Spoolman: remaining as everywhere else, and used is everything that left the spool, so
        // the two add up to the initial weight the way Spoolman clients assume.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/spoolman/api/v1/spool/{spool.SpoolNumber}");
        request.Headers.Authorization = SpoolmanTestSupport.Basic(connection.Secret!);
        var response = await _client.SendAsync(request, Ct);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal(remainingMg / 1000.0, json.GetProperty("remaining_weight").GetDouble(), 3);
        Assert.Equal(consumedMg / 1000.0, json.GetProperty("used_weight").GetDouble(), 3);
        Assert.Equal(InitialMg / 1000.0,
            json.GetProperty("used_weight").GetDouble() + json.GetProperty("remaining_weight").GetDouble(), 3);
    }

    /// <summary>
    /// Two identical prints, one on its own and one in a project, each carrying every kind of row:
    /// weight, length only (with a weight estimate it must NOT fall back to), volume only,
    /// estimate only, and two rows with no spool (a weight, which counts, and a volume, which
    /// has no density to convert with and counts as nothing). Every per-print and per-period
    /// total must count the length and volume rows exactly as the spool's remaining weight does.
    /// </summary>
    [Fact]
    public async Task EveryPerPrintTotal_CountsUsageRecordedOnlyAsALengthOrVolume()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");

        var user = await _support.CreateUserAsync();
        var printerId = await _support.CreatePrinterAsync(user);
        var spool = await AddSpoolAsync(user);

        const double lengthOnlyM = 10;
        const double volumeOnlyMl = 5;
        var actualMg = 100_000 + LengthMg(lengthOnlyM) + VolumeMg(volumeOnlyMl) + 7_000;
        var estimatedMg = 99_999 + 40_000.0;
        var perPrintMg = (long)Math.Round(actualMg + 40_000);
        var perPrintActualMg = (long)Math.Round(actualMg);
        var perPrintEstimatedMg = (long)Math.Round(estimatedMg);
        var spoolPerPrintMg = (long)Math.Round(100_000 + LengthMg(lengthOnlyM) + VolumeMg(volumeOnlyMl) + 40_000);

        Guid projectId = Guid.Empty;
        long standaloneId = 0, inProjectId = 0;
        await WithDbAsync(async db =>
        {
            var project = new Project { Name = "Per-print totals", CreatedById = user.Id, UpdatedById = user.Id };
            db.Projects.Add(project);
            var standalone = NewPrint(user, printerId, "Standalone");
            var inProject = NewPrint(user, printerId, "In a project");
            db.Prints.AddRange(standalone, inProject);
            await db.SaveChangesAsync(Ct);
            inProject.ProjectId = project.Id;
            await db.SaveChangesAsync(Ct);

            foreach (var print in new[] { standalone, inProject })
            {
                db.PrintFilament.AddRange(
                    Usage(print.Id, spool.Id, r => r.AmountMg = 100_000),
                    Usage(print.Id, spool.Id, r => { r.LengthInM = lengthOnlyM; r.EstimatedAmountMg = 99_999; }),
                    Usage(print.Id, spool.Id, r => r.VolumeMl = volumeOnlyMl),
                    Usage(print.Id, spool.Id, r => r.EstimatedAmountMg = 40_000),
                    Usage(print.Id, null, r => r.AmountMg = 7_000),
                    Usage(print.Id, null, r => r.VolumeMl = 3));
            }
            await db.SaveChangesAsync(Ct);

            projectId = project.Id;
            standaloneId = standalone.Id;
            inProjectId = inProject.Id;
        });
        factory.Services.GetRequiredService<ICacheVersionService>().InvalidateUserCache(user.Id);

        var window = new AnalyticsFilter
        {
            TimeZone = "UTC",
            FromDate = DateTimeOffset.UtcNow.AddDays(-7),
            ToDate = DateTimeOffset.UtcNow,
        };

        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;

            // Analytics: overview tile, the Printers tab, the Activity tab.
            var overview = await sp.GetRequiredService<IAnalyticsService>().GetOverview(user.Id, window, Ct);
            Assert.Equal(2 * perPrintMg / 1000.0, overview.Tiles.FilamentGrams.Value!.Value, 3);

            var printers = await sp.GetRequiredService<IPrinterAnalyticsService>().GetPrinters(user.Id, window, Ct);
            Assert.Equal(2 * perPrintMg, printers.Printers.Single(p => p.PrinterId == printerId).MaterialMg);

            var activity = await sp.GetRequiredService<IActivityAnalyticsService>().GetActivity(user.Id, window, Ct);
            Assert.Equal(2 * perPrintMg, activity.Series.Sum(b => b.MaterialMg));

            // MCP: search_prints, get_print, get_print_summary.
            var prints = sp.GetRequiredService<IPrintService>();
            var search = await prints.SearchOwnPrintsForMcp(user.Id, 1, 25, null, null, null, null, null, null, Ct);
            Assert.Equal(2, search.Items.Count);
            Assert.All(search.Items, i => Assert.Equal(perPrintMg / 1000.0, i.MaterialUsedGrams, 3));

            var detail = (await prints.GetOwnPrintDetailForMcp(user.Id, standaloneId, Ct))!;
            Assert.Equal(perPrintMg / 1000.0, detail.MaterialUsedGrams, 3);
            Assert.Equal(perPrintMg / 1000.0, detail.ReturnedMaterialsUsedGrams, 3);
            // The length row's actual wins over its estimate, so it is not an estimate.
            var lengthRow = detail.MaterialsUsed.Single(m => m.EstimatedGrams is > 99 and < 100);
            Assert.False(lengthRow.IsEstimated);
            Assert.Equal(LengthMg(lengthOnlyM) / 1000.0, lengthRow.Grams, 3);
            Assert.Equal(LengthMg(lengthOnlyM) / 1000.0, lengthRow.ActualGrams!.Value, 3);
            Assert.Single(detail.MaterialsUsed, m => m.IsEstimated);

            var summary = await sp.GetRequiredService<IMcpStatisticsService>().GetPrintSummaryForMcp(user.Id, null, null, null, Ct);
            Assert.Equal(2 * perPrintMg / 1000.0, summary.Filtered.MaterialUsedGrams, 3);

            // Achievements: the canonical success material.
            var db = sp.GetRequiredService<PrintLogContext>();
            var rows = await db.Prints.AsNoTracking().Where(p => p.CreatedById == user.Id)
                .Select(p => new PrintRow(p.Id, p.StartDate, p.CreatedDate, p.Status, p.ViewStatus, p.Source, p.Slicer,
                    p.PrintTimeInSeconds, p.EstimatedPrintTimeInSeconds, p.FilamentUsageMg, p.EstimatedFilamentUsageMg))
                .ToListAsync(Ct);
            var aggregate = await PrintAggregate.LoadAsync(db, user.Id, rows, Ct);
            Assert.Equal(2 * perPrintMg, aggregate.SuccessMaterialMg);
        }

        // REST: the anonymous total, the print list, stats, the CSV export.
        var range = $"fromDate={Uri.EscapeDataString(window.FromDate!.Value.ToString("o"))}&toDate={Uri.EscapeDataString(window.ToDate!.Value.ToString("o"))}";
        var total = await RestJsonAsync(user, $"/api/Users/{user.Id}/total-filament-usage?{range}");
        Assert.Equal((2 * perPrintMg).ToString(CultureInfo.InvariantCulture), total.GetProperty("stat").GetString());

        var summaryList = await RestJsonAsync(user, $"/api/Prints/summary?userId={user.Id}");
        var listed = summaryList.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, listed.Count);
        Assert.All(listed, i =>
        {
            Assert.Equal(perPrintMg, i.GetProperty("totalFilamentWeightMg").GetInt64());
            Assert.Equal(perPrintActualMg, i.GetProperty("sumActualFilamentWeightMg").GetInt64());
            Assert.Equal(perPrintEstimatedMg, i.GetProperty("sumEstimatedFilamentWeightMg").GetInt64());
        });

        var stats = await RestJsonAsync(user, $"/api/Prints/stats?{range}");
        Assert.All(stats.EnumerateArray(), s =>
        {
            Assert.Equal(perPrintActualMg, s.GetProperty("filamentUsageMg").GetInt64());
            Assert.Equal(perPrintEstimatedMg, s.GetProperty("estimatedFilamentUsageMg").GetInt64());
        });

        var csv = await (await RestRawAsync(user, "/api/Prints/csv")).Content.ReadAsStringAsync(Ct);
        var csvLines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = csvLines[0].Split(',');
        var actualColumn = Array.IndexOf(header, "Filament Usage (g)");
        var estimatedColumn = Array.IndexOf(header, "Estimated Filament Usage (g)");
        Assert.Equal(3, csvLines.Length);
        Assert.All(csvLines.Skip(1), line =>
        {
            var cells = line.Split(',');
            Assert.Equal(perPrintActualMg / 1000.0, double.Parse(cells[actualColumn], CultureInfo.InvariantCulture), 3);
            Assert.Equal(perPrintEstimatedMg / 1000.0, double.Parse(cells[estimatedColumn], CultureInfo.InvariantCulture), 3);
        });

        // Projects: the detail, the list, and the grouped feed's project and print items.
        var projectDetail = await RestJsonAsync(user, $"/api/Projects/{projectId}");
        Assert.Equal(perPrintMg, projectDetail.GetProperty("totalFilamentWeightMg").GetInt64());

        var projects = await RestJsonAsync(user, "/api/Projects");
        var projectListed = projects.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == projectId);
        Assert.Equal(perPrintMg, projectListed.GetProperty("totalFilamentWeightMg").GetInt64());

        var grouped = (await RestJsonAsync(user, "/api/Prints/grouped")).GetProperty("items").EnumerateArray().ToList();
        var projectItem = grouped.Single(g => g.GetProperty("type").GetString() == "project");
        Assert.Equal(perPrintMg, projectItem.GetProperty("totalFilamentWeightMg").GetInt64());
        Assert.Equal(spoolPerPrintMg, projectItem.GetProperty("filamentUsage").EnumerateArray().Single().GetProperty("amountMg").GetInt64());
        var printItem = grouped.Single(g => g.GetProperty("type").GetString() == "print");
        Assert.Equal(standaloneId, printItem.GetProperty("print").GetProperty("id").GetInt64());
        Assert.Equal(perPrintMg, printItem.GetProperty("print").GetProperty("totalFilamentWeightMg").GetInt64());
        Assert.NotEqual(0, inProjectId);
    }

    /// <summary>The print list's filament-usage sort ranks by the same per-print total.</summary>
    [Fact]
    public async Task PrintList_SortsByFilamentUsage_CountingALengthOnlyPrint()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");

        var user = await _support.CreateUserAsync();
        var printerId = await _support.CreatePrinterAsync(user);
        var spool = await AddSpoolAsync(user);

        long byLength = 0, byWeight = 0;
        await WithDbAsync(async db =>
        {
            var lengthPrint = NewPrint(user, printerId, "By length");    // 100 m is about 298 g
            var weightPrint = NewPrint(user, printerId, "By weight");    // 200 g
            db.Prints.AddRange(lengthPrint, weightPrint);
            await db.SaveChangesAsync(Ct);
            db.PrintFilament.AddRange(
                Usage(lengthPrint.Id, spool.Id, r => r.LengthInM = 100),
                Usage(weightPrint.Id, spool.Id, r => r.AmountMg = 200_000));
            await db.SaveChangesAsync(Ct);
            byLength = lengthPrint.Id;
            byWeight = weightPrint.Id;
        });
        factory.Services.GetRequiredService<ICacheVersionService>().InvalidateUserCache(user.Id);

        var page = await RestJsonAsync(user,
            $"/api/Prints/summary?userId={user.Id}&sortRequest.SortColumn=3&sortRequest.SortDirection=2");
        var order = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ToList();
        Assert.Equal(new[] { byLength, byWeight }, order);
    }

    private static Print NewPrint(User user, long printerId, string title) => new()
    {
        Title = title,
        StartDate = DateTimeOffset.UtcNow.AddDays(-1),
        Status = Print.PrintStatus.Success,
        ViewStatus = Print.PrintViewStatus.Private,
        PrinterId = printerId,
        CreatedById = user.Id,
        UpdatedById = user.Id,
    };

    private static PrintFilament Usage(long printId, Guid? filamentId, Action<PrintFilament> measure)
    {
        var row = new PrintFilament
        {
            PrintId = printId,
            FilamentId = filamentId,
            Source = PrintFilament.SourceMeasurement.Weight,
            EstimatedSource = PrintFilament.SourceMeasurement.Weight,
        };
        measure(row);
        return row;
    }

    private static FilamentAdjustment Adjustment(User user, Guid filamentId, long amountMg) => new()
    {
        FilamentId = filamentId,
        Source = FilamentAdjustment.SourceMeasurement.Weight,
        AmountMg = amountMg,
        CreatedById = user.Id,
        UpdatedById = user.Id,
    };

    private async Task<Filament> AddSpoolAsync(User user)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFilamentService>().AddFilament(new AddFilamentDto
        {
            DisplayName = "Consistency PLA",
            Brand = "Generic",
            MaterialType = "PLA",
            MaterialCategoryNickname = "filament",
            ColorHex = "112233",
            DiameterMm = DiameterMm,
            InitialNominalWeightMg = InitialMg,
            MaterialDensityGramPerCubicCm = Density,
            IsActive = true,
        }, user.Id);
    }

    private async Task WithDbAsync(Func<PrintLogContext, Task> work)
    {
        using var scope = factory.Services.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<PrintLogContext>());
    }

    private async Task<T> RestAsync<T>(User user, string url)
    {
        var response = await RestRawAsync(user, url);
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    private async Task<JsonElement> RestJsonAsync(User user, string url)
    {
        var response = await RestRawAsync(user, url);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> RestRawAsync(User user, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        var response = await _client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return response;
    }
}
