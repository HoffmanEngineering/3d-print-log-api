using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

/// <summary>End-to-end tests for the create_print tool over the /mcp endpoint.</summary>
public class CreatePrintToolTests : IClassFixture<McpDataWebApplicationFactory>
{
    private readonly McpDataWebApplicationFactory _factory;

    public CreatePrintToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

    private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };

    [Fact]
    public async Task CreatePrint_ForeignPrinter_ReturnsNotFound()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

        var code = await McpDataWebApplicationFactory.ToolErrorCode(client, "create_print",
            new Dictionary<string, object?>
            {
                ["title"] = "x",
                ["printerId"] = McpTestData.OtherPrinterId, // another user's printer
                ["status"] = "Success",
                ["idempotencyKey"] = "tool-foreign",
            });

        Assert.Equal("not_found", code);
    }

    [Fact]
    public async Task CreatePrint_DoesNotUnloadOtherFilaments()
    {
        // The primary user's SearchPrinterId has "Long PLA" (aaaa-1002) currently loaded. Logging a
        // print that consumes a DIFFERENT material must not unload it.
        var loadedFilamentId = new Guid("aaaaaaaa-1002-0000-0000-000000000000");

        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var result = await client.CallToolAsync("create_print", new Dictionary<string, object?>
        {
            ["title"] = "Side-effect check",
            ["printerId"] = McpTestData.SearchPrinterId,
            ["status"] = "Success",
            ["idempotencyKey"] = "tool-side-effect",
            ["materials"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["materialId"] = IntegrationTestSeeder.TestFilamentId1,
                    ["source"] = "Weight",
                    ["amount"] = 5.0,
                },
            },
        });
        Assert.True(result.IsError != true);

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var stillLoaded = ctx.Set<PrinterFilament>().Any(pf =>
            pf.PrinterId == McpTestData.SearchPrinterId &&
            pf.FilamentId == loadedFilamentId &&
            pf.UnloadedDateTime == null);

        Assert.True(stillLoaded, "logging a print must not unload previously-loaded filaments");
    }
}
