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
            await db.SaveChangesAsync();
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
}
