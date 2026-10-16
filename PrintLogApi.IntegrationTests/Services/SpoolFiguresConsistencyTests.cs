using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.IntegrationTests.Spoolman;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Services;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// One spool, every kind of usage row and adjustment, and every place the product reports how
/// much is left on it. They must all agree, because they are all supposed to be the one rule in
/// <c>FilamentProfile</c>. Each surface used to carry its own copy of that rule, and two had
/// drifted: <c>get_printer</c> ignored usage recorded only as a length or volume, and the
/// Spoolman API counted only prints as used, so a weigh-in lowered <c>remaining_weight</c>
/// without raising <c>used_weight</c>.
///
/// <para>The usage rows are written directly rather than through PrintService on purpose:
/// PrintService fills every measure in, which would hide a surface that reads only AmountMg.</para>
/// </summary>
public class SpoolFiguresConsistencyTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private const double Density = 1.24;
    private const double DiameterMm = 1.75;
    private const long InitialMg = 1_000_000;

    private readonly HttpClient _client = factory.CreateClient();
    private readonly SpoolmanTestSupport _support = new(factory.Services);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EverySurface_ReportsTheSameRemaining_CountingPrintsAndAdjustments()
    {
        var user = await _support.CreateUserAsync();
        var printerId = await _support.CreatePrinterAsync(user);
        var connection = await _support.ConnectAsync(user, printerId);
        var spool = await AddSpoolAsync(user);

        var (printsMg, netAdjustmentMg) = await ArrangeMixedUsageAsync(user, printerId, spool);
        var consumedMg = (long)Math.Round(printsMg - netAdjustmentMg);
        var remainingMg = (long)Math.Round(InitialMg - (printsMg - netAdjustmentMg));

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
            Assert.Equal(remainingMg / 1000.0, inventory.Items.Single().RemainingGrams!.Value, 3);

            var material = await filaments.GetOwnMaterialDetailForMcp(user.Id, spool.Id, Ct);
            Assert.Equal(remainingMg / 1000.0, material.RemainingGrams!.Value, 3);

            var mcpPrinter = await scope.ServiceProvider.GetRequiredService<IPrinterService>().GetPrinterForMcp(user.Id, printerId, Ct);
            Assert.Equal(remainingMg / 1000.0, mcpPrinter.LoadedFilaments.Single().RemainingGrams!.Value, 3);

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
    /// A spool with no nominal weight has no remaining amount to report, and every surface says
    /// so with null (#230). Before that, the detail page said null while the list, the printer
    /// page and the MCP tools counted from zero and reported minus whatever had been consumed.
    /// The usage is the same mix as above, so a surface still counting from zero would show a
    /// clearly negative figure rather than a coincidental 0.
    /// </summary>
    [Fact]
    public async Task EverySurface_ReportsNullRemaining_ForAnUntrackedSpool()
    {
        var user = await _support.CreateUserAsync();
        var printerId = await _support.CreatePrinterAsync(user);
        var connection = await _support.ConnectAsync(user, printerId);
        var spool = await AddSpoolAsync(user, initialMg: null);
        var (printsMg, netAdjustmentMg) = await ArrangeMixedUsageAsync(user, printerId, spool);
        var consumedMg = (long)Math.Round(printsMg - netAdjustmentMg);

        // REST: the detail page, the list, and the printer page's loaded spool (mapped in memory).
        var detail = await RestAsync<FilamentDetailDto>(user, $"/api/Filaments/{spool.Id}");
        Assert.Null(detail.FilamentRemaining);

        var list = await RestJsonAsync(user, "/api/Filaments");
        var listed = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == spool.Id);
        AssertNull(listed, "filamentRemaining");
        AssertNull(listed, "filamentLengthRemainingInM");
        AssertNull(listed, "filamentVolumeRemainingInMl");

        var printer = await RestJsonAsync(user, $"/api/Printers/{printerId}");
        var loadedRest = printer.GetProperty("loadedFilaments").EnumerateArray().Single().GetProperty("filament");
        AssertNull(loadedRest, "filamentRemaining");

        // MCP: get_material_inventory, get_material, get_printer, and find_material leaves it out.
        using (var scope = factory.Services.CreateScope())
        {
            var filaments = scope.ServiceProvider.GetRequiredService<IFilamentService>();
            var inventory = await filaments.GetMaterialInventoryForMcp(user.Id, 1, 25, null, null, true, Ct);
            Assert.Null(inventory.Items.Single().RemainingGrams);

            var material = await filaments.GetOwnMaterialDetailForMcp(user.Id, spool.Id, Ct);
            Assert.Null(material.RemainingGrams);
            Assert.False(material.HasNominalCapacity);

            var found = await filaments.FindMaterialForMcp(user.Id, "PLA", null, 1, Ct);
            Assert.Empty(found.Groups);

            var mcpPrinter = await scope.ServiceProvider.GetRequiredService<IPrinterService>().GetPrinterForMcp(user.Id, printerId, Ct);
            Assert.Null(mcpPrinter.LoadedFilaments.Single().RemainingGrams);

            // Analytics: the Materials tab's top spools.
            var materials = await scope.ServiceProvider.GetRequiredService<IMaterialAnalyticsService>()
                .GetMaterials(user.Id, new AnalyticsFilter { TimeZone = "UTC" }, Ct);
            Assert.All(materials.TopSpools.Where(s => s.FilamentId == spool.Id.ToString()), s => Assert.Null(s.RemainingMg));
        }

        // Spoolman: no remaining_weight, but used_weight still counts everything that left it.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/spoolman/api/v1/spool/{spool.SpoolNumber}");
        request.Headers.Authorization = SpoolmanTestSupport.Basic(connection.Secret!);
        var response = await _client.SendAsync(request, Ct);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.False(json.TryGetProperty("remaining_weight", out _));
        Assert.Equal(consumedMg / 1000.0, json.GetProperty("used_weight").GetDouble(), 3);
    }

    private static void AssertNull(JsonElement element, string property) =>
        Assert.True(
            !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null,
            $"{property} should be null, was {value}");

    /// <summary>
    /// Logs one print against the spool with a usage row in every measure, two adjustments, and
    /// loads the spool on the printer. Returns the print usage and the net adjustment in mg.
    /// </summary>
    private async Task<(double PrintsMg, long NetAdjustmentMg)> ArrangeMixedUsageAsync(User user, long printerId, Filament spool)
    {
        const double lengthOnlyM = 10;
        const double volumeOnlyMl = 5;
        var printsMg = 100_000                                                      // actual weight
            + 250.0 * Math.PI * Density * DiameterMm * DiameterMm * lengthOnlyM     // actual length only
            + volumeOnlyMl * Density * 1000.0                                       // actual volume only
            + 40_000;                                                               // estimate only
        const long netAdjustmentMg = -150_000 + 20_000;                             // a weigh-in, then a correction back up

        await WithDbAsync(async db =>
        {
            var print = new Print
            {
                Title = "Mixed usage",
                StartDate = DateTimeOffset.UtcNow.AddDays(-1),
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
                PrinterId = printerId,
                CreatedById = user.Id,
                UpdatedById = user.Id,
            };
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
        return (printsMg, netAdjustmentMg);
    }

    private static PrintFilament Usage(long printId, Guid filamentId, Action<PrintFilament> measure)
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

    private async Task<Filament> AddSpoolAsync(User user, long? initialMg = InitialMg)
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
            InitialNominalWeightMg = initialMg,
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
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode}");
        return response;
    }
}
