using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

public class GetPrintToolTests : IClassFixture<McpDataWebApplicationFactory>
{
    private const string ToolName = "get_print";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly McpDataWebApplicationFactory _factory;

    public GetPrintToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

    private sealed record Usage(Guid? FilamentId, string Name, string Brand, string Material,
        string Color, double Grams, bool IsEstimated);

    private sealed record Detail(long Id, string Title, string Status, long? PrinterId,
        string PrinterName, DateTimeOffset? StartedAt, double MaterialUsedGrams, int? DurationSeconds,
        bool DurationIsEstimated, bool MaterialIsEstimated,
        decimal? EstimatedCost, string Notes, Guid? ProjectId, string ProjectName,
        List<Usage> MaterialsUsed, bool MaterialsUsedTruncated, double ReturnedMaterialsUsedGrams);

    private static (Detail detail, string rawJson) Parse(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        return (JsonSerializer.Deserialize<Detail>(text, JsonOptions)!, text);
    }

    [Fact]
    public async Task ForeignProjectAndPrinter_AreNotLeaked_OnTheCallersOwnPrint()
    {
        // Same rule the filament rows already follow: gate related data on OWNERSHIP, not merely
        // on the navigation being non-null, or a corrupt cross-owner row leaks another user's
        // project and printer names.
        await using var client = await _factory.ConnectAsync();
        var (detail, rawJson) = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["id"] = McpTestData.CrossOwnerRefPrintId }));

        Assert.Null(detail.ProjectName);
        Assert.Null(detail.ProjectId);
        Assert.Null(detail.PrinterName);
        Assert.Null(detail.PrinterId);
        Assert.DoesNotContain("SECRET FOREIGN PROJECT", rawJson);
        Assert.DoesNotContain("Other User Printer", rawJson);
    }

    [Fact]
    public async Task MaterialsUsed_ReportsEachColorOfADualColorPrint()
    {
        // The motivating failure: a print named "Dual Color 3D Benchy" could not report which
        // two colours it used, because only an aggregate gram total was returned.
        await using var client = await _factory.ConnectAsync();
        var (detail, _) = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["id"] = McpTestData.DualColorPrintId }));

        var named = detail.MaterialsUsed.Where(m => m.Color != null).ToList();
        Assert.Equal(2, named.Count);
        Assert.Contains(named, m => m.Color == "Blue" && m.Material == "PLA");
        Assert.Contains(named, m => m.Color == "Navy" && m.Material == "PLA+");
    }

    [Fact]
    public async Task MaterialsUsed_PreservesRowsWithNoFilamentReference()
    {
        // PrintFilament.FilamentId is nullable. An inner join would silently drop these rows.
        await using var client = await _factory.ConnectAsync();
        var (detail, _) = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["id"] = McpTestData.DualColorPrintId }));

        Assert.Equal(4, detail.MaterialsUsed.Count);
        Assert.Contains(detail.MaterialsUsed, m => m.FilamentId is null && m.Grams > 0);
    }

    [Fact]
    public async Task MaterialsUsed_ZeroActual_FallsBackToEstimate()
    {
        // A zero (or negative) AmountMg must fall through to the estimate. `AmountMg ?? Est`
        // would take the zero at face value and break the sum invariant.
        await using var client = await _factory.ConnectAsync();
        var (detail, _) = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["id"] = McpTestData.DualColorPrintId }));

        var estimated = detail.MaterialsUsed.Where(m => m.IsEstimated).ToList();
        Assert.Equal(2, estimated.Count);               // the orphan row and the zero-actual row
        Assert.Contains(estimated, m => m.Grams == 4d); // 4000 mg estimate, not 0
    }

    [Fact]
    public async Task MaterialsUsed_SumsToTheReportedTotal()
    {
        // The parts must add up to the whole, or an agent reading both will contradict itself.
        await using var client = await _factory.ConnectAsync();
        var (detail, _) = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["id"] = McpTestData.DualColorPrintId }));

        Assert.False(detail.MaterialsUsedTruncated);
        Assert.Equal(detail.MaterialUsedGrams, detail.MaterialsUsed.Sum(m => m.Grams), 3);
        Assert.Equal(detail.MaterialUsedGrams, detail.ReturnedMaterialsUsedGrams, 3);
        Assert.Equal(64d, detail.MaterialUsedGrams); // 30 + 20 + 10 + 4
    }

    [Fact]
    public async Task MaterialsUsed_ForeignSpool_IsRedactedButGramsPreserved()
    {
        // Corrupt cross-owner row. Guarding only on "navigation is not null" would leak the
        // other user's brand, material and colour.
        await using var client = await _factory.ConnectAsync();
        var (detail, rawJson) = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["id"] = McpTestData.ForeignSpoolPrintId }));

        var usage = Assert.Single(detail.MaterialsUsed);
        Assert.Null(usage.FilamentId);
        Assert.Null(usage.Name);
        Assert.Null(usage.Brand);
        Assert.Null(usage.Material);
        Assert.Null(usage.Color);

        // Grams survive: dropping the row would break the sum invariant.
        Assert.Equal(7d, usage.Grams);
        Assert.Equal(7d, detail.MaterialUsedGrams);

        Assert.DoesNotContain("OTHER USER SPOOL", rawJson);
        Assert.DoesNotContain("Secret Purple", rawJson);
    }

    [Fact]
    public async Task Creator_CanReadOwnPrint()
    {
        await using var client = await _factory.ConnectAsync();
        var result = await client.CallToolAsync(ToolName, new Dictionary<string, object?> { ["id"] = McpTestData.RichPrintId1 });
        var (detail, _) = Parse(result);

        Assert.Equal(McpTestData.RichPrintId1, detail.Id);
        Assert.Equal("Rich Print 1", detail.Title);
        Assert.Equal("Success", detail.Status);
        Assert.Equal(25.0, detail.MaterialUsedGrams);
        Assert.Equal(7200, detail.DurationSeconds);
    }

    [Fact]
    public async Task Caller_CannotReadForeignPublicPrint()
    {
        // ForeignPrintId is Public but owned by another user: creator-only => not found.
        await using var client = await _factory.ConnectAsync();
        Assert.True(await McpDataWebApplicationFactory.IsToolError(
            client, ToolName, new() { ["id"] = McpTestData.ForeignPrintId }));
    }

    [Fact]
    public async Task MissingPrint_IsError()
    {
        await using var client = await _factory.ConnectAsync();
        Assert.True(await McpDataWebApplicationFactory.IsToolError(
            client, ToolName, new() { ["id"] = 999999L }));
    }

    [Fact]
    public async Task Detail_ExcludesImagesCommentsAndFiles()
    {
        await using var client = await _factory.ConnectAsync();
        var result = await client.CallToolAsync(ToolName, new Dictionary<string, object?> { ["id"] = McpTestData.RichPrintId1 });
        var (_, rawJson) = Parse(result);

        // fileName/url/allowComments/allowFileDownloads are deliberately exposed (they are the
        // print's own metadata and visibility toggles). What must never appear is the CONTENT of
        // the image, attachment and comment collections, or the file hash.
        foreach (var forbidden in new[] { "image", "\"comments\"", "fileHash", "attachment" })
        {
            Assert.DoesNotContain(forbidden, rawJson, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Duration_UsesTheEstimate_AndFlagsIt()
    {
        await using var client = await _factory.ConnectAsync(McpTestData.MetricsUserOAuthId);
        var (detail, _) = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["id"] = McpTestData.EstimatedOnlyPrintId }));

        Assert.Equal(6933, detail.DurationSeconds);
        Assert.True(detail.DurationIsEstimated);
    }

    [Fact]
    public async Task Duration_StoredZero_FallsBackToTheEstimate()
    {
        // A stored 0 IS HasValue, so a ??-coalescing reader reports 0 and suppresses the estimate.
        await using var client = await _factory.ConnectAsync(McpTestData.MetricsUserOAuthId);
        var (detail, _) = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["id"] = McpTestData.ZeroActualPrintId }));

        Assert.Equal(1800, detail.DurationSeconds);   // NOT 0
        Assert.True(detail.DurationIsEstimated);
    }

    [Fact]
    public async Task Duration_ActualWins_AndIsNotFlagged()
    {
        await using var client = await _factory.ConnectAsync(McpTestData.MetricsUserOAuthId);
        var (detail, _) = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["id"] = McpTestData.ActualWinsPrintId }));

        Assert.Equal(7200, detail.DurationSeconds);
        Assert.False(detail.DurationIsEstimated);
    }

    [Fact]
    public async Task Duration_NeitherRecorded_IsNull_AndNotFlaggedEstimated()
    {
        // Null, not 0: reporting 0 would assert a measurement of zero seconds.
        await using var client = await _factory.ConnectAsync(McpTestData.MetricsUserOAuthId);
        var (detail, _) = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["id"] = McpTestData.NoDurationPrintId }));

        Assert.Null(detail.DurationSeconds);
        Assert.False(detail.DurationIsEstimated);
    }

    [Fact]
    public async Task MaterialIsEstimated_IsTrue_WhenTheUsageRowFellBack()
    {
        await using var client = await _factory.ConnectAsync(McpTestData.MetricsUserOAuthId);
        var (detail, _) = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["id"] = McpTestData.EstimatedOnlyPrintId }));

        Assert.True(detail.MaterialIsEstimated);
        Assert.Contains(detail.MaterialsUsed, m => m.IsEstimated);
    }

    [Fact]
    public async Task MaterialIsEstimated_IsFalse_WhenTheActualWasMeasured()
    {
        await using var client = await _factory.ConnectAsync(McpTestData.MetricsUserOAuthId);
        var (detail, _) = Parse(await client.CallToolAsync(ToolName,
            new Dictionary<string, object?> { ["id"] = McpTestData.ActualWinsPrintId }));

        Assert.False(detail.MaterialIsEstimated);
    }
}
