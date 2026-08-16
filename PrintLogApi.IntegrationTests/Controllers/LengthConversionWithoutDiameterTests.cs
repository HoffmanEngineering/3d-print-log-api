using System.Net;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// The measurement conversions read DiameterMm.Value on their Length branch. Their enclosing
/// guard only rejects a missing diameter when the material category TRACKS one
/// (`hasDiameter && !DiameterMm.HasValue`), so a resin - HasDiameter = false, DiameterMm null -
/// walks straight past it and throws InvalidOperationException.
///
/// The Volume and Weight branches in the same methods already guard the identical access. Only
/// the Length branch was missed, in both PrintService and FilamentService.
/// </summary>
public class LengthConversionWithoutDiameterTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public LengthConversionWithoutDiameterTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    private IPrintService PrintSvc(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IPrintService>();

    private static Print ResinPrintWith(PrintFilament usage) => new()
    {
        FilamentUsage = new List<PrintFilament> { usage },
    };

    [Fact]
    public async Task UpdateFilamentUsageWeights_LengthSourceOnResin_LeavesDerivedFieldsNull()
    {
        using var scope = _factory.Services.CreateScope();
        var print = ResinPrintWith(new PrintFilament
        {
            FilamentId = IntegrationTestSeeder.TestResinFilamentId,
            Source = PrintFilament.SourceMeasurement.Length,
            LengthInM = 12.5,
        });

        await PrintSvc(scope).UpdateFilamentUsageWeights(print);

        // Not computable without a diameter, so nothing is derived - matching what the Volume
        // and Weight branches do for the same material.
        var row = print.FilamentUsage!.Single();
        Assert.Null(row.AmountMg);
        Assert.Null(row.VolumeMl);
    }

    [Fact]
    public async Task UpdateFilamentUsageWeights_EstimatedLengthSourceOnResin_LeavesDerivedFieldsNull()
    {
        using var scope = _factory.Services.CreateScope();
        var print = ResinPrintWith(new PrintFilament
        {
            FilamentId = IntegrationTestSeeder.TestResinFilamentId,
            EstimatedSource = PrintFilament.SourceMeasurement.Length,
            EstimatedLengthInM = 12.5,
        });

        await PrintSvc(scope).UpdateFilamentUsageWeights(print);

        var row = print.FilamentUsage!.Single();
        Assert.Null(row.EstimatedAmountMg);
        Assert.Null(row.EstimatedVolumeMl);
    }

    [Fact]
    public async Task UpdateFilamentUsageWeights_LengthSourceOnFilament_StillComputes()
    {
        // The guard must not swallow the case it was always meant to handle: a material that
        // does track a diameter still derives both fields from a length.
        using var scope = _factory.Services.CreateScope();
        var print = ResinPrintWith(new PrintFilament
        {
            FilamentId = IntegrationTestSeeder.TestFilamentId1,
            Source = PrintFilament.SourceMeasurement.Length,
            LengthInM = 12.5,
        });

        await PrintSvc(scope).UpdateFilamentUsageWeights(print);

        var row = print.FilamentUsage!.Single();
        Assert.NotNull(row.AmountMg);
        Assert.True(row.AmountMg > 0);
        Assert.NotNull(row.VolumeMl);
    }

    [Fact]
    public async Task CreatePrint_LengthSourceOnResin_ReturnsCreated()
    {
        // The REST entry point that makes the above reachable in production.
        var newPrint = new AddPrintDTO
        {
            Title = "Print With Length-Measured Resin",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true,
            FilamentUsage = new List<PrintFilamentSummaryDto>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Filament = new FilamentSummaryDto { Id = IntegrationTestSeeder.TestResinFilamentId },
                    Source = PrintFilament.SourceMeasurement.Length,
                    LengthInM = 12.5,
                },
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newPrint);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected 201, got {(int)response.StatusCode}. Body: {body}");
    }
}
