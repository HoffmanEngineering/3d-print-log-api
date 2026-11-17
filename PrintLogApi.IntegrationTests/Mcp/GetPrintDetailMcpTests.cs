using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

public class GetPrintDetailMcpTests : IClassFixture<McpDataWebApplicationFactory>
{
    private readonly McpDataWebApplicationFactory _factory;
    public GetPrintDetailMcpTests(McpDataWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Detail_ReturnsNewFields_AndPerRowGrams()
    {
        long printId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var print = new Print
            {
                Title = "RT",
                PrinterId = McpTestData.SearchPrinterId,
                Status = Print.PrintStatus.Success,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                FileName = "rt.gcode",
                Url = "https://x",
                ViewStatus = Print.PrintViewStatus.Unlisted,
                EstimatedPrintTimeInSeconds = 3300,
                AllowComments = true,
                AllowFileDownloads = true,
                FilamentUsage = new System.Collections.Generic.List<PrintFilament>
                {
                    new()
                    {
                        FilamentId = IntegrationTestSeeder.TestFilamentId1,
                        Source = PrintFilament.SourceMeasurement.Weight,
                        AmountMg = 20000,
                        EstimatedSource = PrintFilament.SourceMeasurement.Weight,
                        EstimatedAmountMg = 19000,
                        Notes = "row note",
                    },
                },
            };
            db.Prints.Add(print);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            printId = print.Id;
        }

        using var s2 = _factory.Services.CreateScope();
        var svc = s2.ServiceProvider.GetRequiredService<IPrintService>();
        var detail = await svc.GetOwnPrintDetailForMcp(IntegrationTestSeeder.TestUserId, printId, CancellationToken.None);

        Assert.Equal("rt.gcode", detail!.FileName);
        Assert.Equal("https://x", detail.Url);
        Assert.Equal("Unlisted", detail.ViewStatus);
        Assert.Equal(3300, detail.EstimatedDurationSeconds);
        Assert.True(detail.AllowComments);
        Assert.True(detail.AllowFileDownloads);
        var row = Assert.Single(detail.MaterialsUsed);
        Assert.Equal(20.0, row.ActualGrams);
        Assert.Equal(19.0, row.EstimatedGrams);
        Assert.Equal("row note", row.Notes);
    }

    [Fact]
    public async Task Detail_VolumeRow_ReportsMilliliters_BesideTheGrams()
    {
        long printId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var print = new Print
            {
                Title = "Resin",
                PrinterId = McpTestData.SearchPrinterId,
                Status = Print.PrintStatus.Success,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                FilamentUsage = new System.Collections.Generic.List<PrintFilament>
                {
                    // As /complete stores a bridge's resin row: the volume, and the weight derived from it.
                    new()
                    {
                        FilamentId = IntegrationTestSeeder.TestResinFilamentId,
                        Slot = 0,
                        Source = PrintFilament.SourceMeasurement.Volume,
                        VolumeMl = 42.5,
                        AmountMg = 46750,
                        EstimatedSource = PrintFilament.SourceMeasurement.Volume,
                        EstimatedVolumeMl = 40,
                        EstimatedAmountMg = 44000,
                    },
                    // An estimate-only volume row resolves to its estimated mL.
                    new()
                    {
                        Slot = 1,
                        Source = PrintFilament.SourceMeasurement.Weight,
                        EstimatedSource = PrintFilament.SourceMeasurement.Volume,
                        EstimatedVolumeMl = 12.5,
                    },
                    new()
                    {
                        FilamentId = IntegrationTestSeeder.TestFilamentId1,
                        Source = PrintFilament.SourceMeasurement.Weight,
                        AmountMg = 20000,
                        VolumeMl = 16.1,
                        EstimatedSource = PrintFilament.SourceMeasurement.Weight,
                    },
                },
            };
            db.Prints.Add(print);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            printId = print.Id;
        }

        using var s2 = _factory.Services.CreateScope();
        var svc = s2.ServiceProvider.GetRequiredService<IPrintService>();
        var detail = await svc.GetOwnPrintDetailForMcp(IntegrationTestSeeder.TestUserId, printId, CancellationToken.None);

        var resin = Assert.Single(detail!.MaterialsUsed, r => r.FilamentId == IntegrationTestSeeder.TestResinFilamentId);
        Assert.Equal(46.75, resin.Grams);
        Assert.Equal(42.5, resin.Milliliters);
        Assert.False(resin.IsEstimated);
        var estimateOnly = Assert.Single(detail.MaterialsUsed, r => r.FilamentId is null);
        Assert.Equal(12.5, estimateOnly.Milliliters);
        // A row measured by weight reports grams only, even when a volume was derived for it.
        var pla = Assert.Single(detail.MaterialsUsed, r => r.FilamentId == IntegrationTestSeeder.TestFilamentId1);
        Assert.Null(pla.Milliliters);
    }
}
