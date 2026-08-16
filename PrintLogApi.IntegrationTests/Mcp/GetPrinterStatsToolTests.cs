using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

public class GetPrinterStatsToolTests : IClassFixture<McpDataWebApplicationFactory>
{
    private const string ToolName = "get_printer_stats";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly McpDataWebApplicationFactory _factory;

    public GetPrinterStatsToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

    private sealed record Stat(long PrinterId, string PrinterName, int TotalPrints,
        int SuccessfulPrints, int FailedPrints, double SuccessRatePercent, int TotalPrintTimeSeconds,
        int PrintsWithEstimatedDuration);

    private sealed record PageResult(List<Stat> Items, int Page, int PageSize, int TotalCount, int TotalPages);

    // The tool is now paginated: removing the date bound without bounding the result would let a
    // user with many printers pull an unbounded list.
    private static List<Stat> Parse(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        return JsonSerializer.Deserialize<PageResult>(text, JsonOptions)!.Items;
    }

    private static PageResult ParsePage(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        return JsonSerializer.Deserialize<PageResult>(text, JsonOptions)!;
    }

    private static readonly DateTimeOffset FullFrom = McpTestData.RichPrint2Date.AddDays(-30);
    private static readonly DateTimeOffset FullTo = McpTestData.RichPrint2Date.AddDays(1);

    [Fact]
    public async Task Stats_CountStatusesDurationAndRate_OrderedByName()
    {
        await using var client = await _factory.ConnectAsync();
        var stats = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["from"] = FullFrom, ["to"] = FullTo }));

        Assert.Equal(2, stats.Count);
        Assert.Equal(new[] { IntegrationTestSeeder.TestPrinterId, IntegrationTestSeeder.TestPrinterId2 },
            stats.Select(s => s.PrinterId).ToArray());

        var printer1 = stats.Single(s => s.PrinterId == IntegrationTestSeeder.TestPrinterId);
        Assert.Equal(5, printer1.TotalPrints);
        Assert.Equal(2, printer1.SuccessfulPrints);
        Assert.Equal(0, printer1.FailedPrints);
        Assert.Equal(40.0, printer1.SuccessRatePercent);
        // Was 0: all 5 base prints are estimate-only (EstimatedPrintTimeInSeconds = 3600*i, no
        // actual), so 3600*15 = 54000. Asserting 0 here was asserting the bug.
        Assert.Equal(54000, printer1.TotalPrintTimeSeconds);
        Assert.Equal(5, printer1.PrintsWithEstimatedDuration);

        var printer2 = stats.Single(s => s.PrinterId == IntegrationTestSeeder.TestPrinterId2);
        Assert.Equal(2, printer2.TotalPrints);
        Assert.Equal(1, printer2.SuccessfulPrints);
        Assert.Equal(1, printer2.FailedPrints);
        Assert.Equal(50.0, printer2.SuccessRatePercent);
        // Both rich prints have real actuals, so this total is unchanged and nothing is estimated.
        Assert.Equal(10800, printer2.TotalPrintTimeSeconds);
        Assert.Equal(0, printer2.PrintsWithEstimatedDuration);
    }

    [Fact]
    public async Task DateRange_ExcludesOutOfRangePrints()
    {
        await using var client = await _factory.ConnectAsync();
        // Narrow window covering only the two rich prints (both on Printer 2).
        var stats = Parse(await client.CallToolAsync(ToolName, new Dictionary<string, object?>
        {
            ["from"] = McpTestData.RichPrint1Date.AddHours(-1),
            ["to"] = McpTestData.RichPrint2Date.AddHours(1),
        }));

        Assert.Single(stats);
        Assert.Equal(IntegrationTestSeeder.TestPrinterId2, stats[0].PrinterId);
        Assert.Equal(2, stats[0].TotalPrints);
    }

    [Fact]
    public async Task OwnerIsolation_OtherUserSeesOnlyOwnPrinter()
    {
        await using var client = await _factory.ConnectAsync(McpTestData.OtherUserOAuthId);
        var stats = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["from"] = FullFrom, ["to"] = FullTo }));

        Assert.Single(stats);
        Assert.Equal(McpTestData.OtherPrinterId, stats[0].PrinterId);
    }

    [Fact]
    public async Task OverlongRange_IsError()
    {
        await using var client = await _factory.ConnectAsync();
        Assert.True(await McpDataWebApplicationFactory.IsToolError(client, ToolName,
            new() { ["from"] = FullFrom, ["to"] = FullFrom.AddDays(367) }));
    }

    [Fact]
    public async Task Exactly366Days_IsAccepted()
    {
        await using var client = await _factory.ConnectAsync();
        Assert.False(await McpDataWebApplicationFactory.IsToolError(client, ToolName,
            new() { ["from"] = FullFrom, ["to"] = FullFrom.AddDays(366) }));
    }

    [Fact]
    public async Task AllTime_WhenRangeOmitted()
    {
        // "How many prints has this printer done, ever?" previously required looping year by
        // year because the range was mandatory and capped at 366 days.
        await using var client = await _factory.ConnectAsync();
        var stats = Parse(await client.CallToolAsync(ToolName, new Dictionary<string, object?>()));

        Assert.NotEmpty(stats);
        // The undated search fixtures live on their own printer, so all-time sees it while any
        // ranged query does not.
        Assert.Contains(stats, s => s.PrinterId == McpTestData.SearchPrinterId);
    }

    [Fact]
    public async Task FilterByPrinterId_ReturnsOnlyThatPrinter()
    {
        await using var client = await _factory.ConnectAsync();
        var stats = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["printerId"] = IntegrationTestSeeder.TestPrinterId2 }));

        var stat = Assert.Single(stats);
        Assert.Equal(IntegrationTestSeeder.TestPrinterId2, stat.PrinterId);
    }

    [Fact]
    public async Task Results_ArePaginated()
    {
        await using var client = await _factory.ConnectAsync();
        var page = ParsePage(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["pageSize"] = 1 }));

        Assert.Single(page.Items);
        Assert.True(page.TotalCount > 1);
        Assert.Equal(page.TotalCount, page.TotalPages); // pageSize 1
    }

    [Fact]
    public async Task HalfOpenRange_IsError()
    {
        await using var client = await _factory.ConnectAsync();
        Assert.True(await McpDataWebApplicationFactory.IsToolError(
            client, ToolName, new() { ["from"] = FullFrom }));
    }

    [Fact]
    public async Task PrinterStats_Duration_UsesTheEstimate_AndCountsIt()
    {
        await using var client = await _factory.ConnectAsync(McpTestData.MetricsUserOAuthId);
        var stats = Parse(await client.CallToolAsync(ToolName, new Dictionary<string, object?>()));

        var printer = Assert.Single(stats);   // the metrics user owns exactly one printer
        Assert.Equal(McpTestData.DurationMatrixTotalSeconds, printer.TotalPrintTimeSeconds);
        Assert.Equal(McpTestData.DurationMatrixEstimatedCount, printer.PrintsWithEstimatedDuration);
    }
}
