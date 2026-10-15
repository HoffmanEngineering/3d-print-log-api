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

        const double lengthOnlyM = 10;
        const double volumeOnlyMl = 5;
        var printsMg = 100_000                                                      // actual weight
            + 250.0 * Math.PI * Density * DiameterMm * DiameterMm * lengthOnlyM     // actual length only
            + volumeOnlyMl * Density * 1000.0                                       // actual volume only
            + 40_000;                                                               // estimate only
        const long netAdjustmentMg = -150_000 + 20_000;                             // a weigh-in, then a correction back up
        var consumedMg = (long)Math.Round(printsMg - netAdjustmentMg);
        var remainingMg = (long)Math.Round(InitialMg - (printsMg - netAdjustmentMg));

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
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode}");
        return response;
    }
}
