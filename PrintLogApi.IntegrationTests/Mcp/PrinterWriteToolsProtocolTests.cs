using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// End-to-end checks over the real /mcp endpoint for the printer write surface: tool naming,
    /// annotations, scope visibility, and the guarantee that a WRITE-ONLY token can verify
    /// everything it wrote from the tool's own response — without ever holding the read scope.
    /// </summary>
    public class PrinterWriteToolsProtocolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;
        public PrinterWriteToolsProtocolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };
        private static readonly string[] WriteOnly = { "write:printdata" };
        private static readonly string[] ReadOnly = { "read:printdata" };

        private static string RawText(CallToolResult result) =>
            result.Content.OfType<TextContentBlock>().First().Text;

        private static Dictionary<string, object?> BasicArgs(string name) => new()
        {
            ["make"] = "Bambu",
            ["model"] = "X1C",
            ["name"] = name,
        };

        [Fact]
        public async Task ToolList_ExposesThePrinterWriteTools_WithAnnotations()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
            var tools = await client.ListToolsAsync();

            var create = Assert.Single(tools.Where(t => t.Name == "create_printer"));
            Assert.False(create.ProtocolTool.Annotations?.DestructiveHint);
            // The key is OPTIONAL, so a no-key call is genuinely not idempotent. false is the honest
            // static hint; claiming true would tell a client a blind retry is safe when it is not.
            Assert.False(create.ProtocolTool.Annotations?.IdempotentHint);

            var update = Assert.Single(tools.Where(t => t.Name == "update_printer"));
            // True, matching update_print: 'destructive' in MCP means an update may overwrite or
            // discard existing data, not that it deletes the entity. This tool overwrites fields and
            // honours 'clear', so a client must not treat a blind retry as free.
            Assert.True(update.ProtocolTool.Annotations?.DestructiveHint);
        }

        [Fact]
        public async Task ReadOnlyToken_CannotSeeThePrinterWriteTools()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadOnly);
            var names = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet();
            Assert.DoesNotContain("create_printer", names);
            Assert.DoesNotContain("update_printer", names);
            Assert.Contains("get_printer", names);
        }

        [Fact]
        public async Task WriteOnlyToken_ReadsBackEveryFieldFromTheToolResult()
        {
            // The point of returning full detail: an agent holding ONLY write:printdata must be able
            // to confirm what it wrote without get_printer, which it cannot call.
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, WriteOnly);

            var args = BasicArgs("protocol-printer");
            args["description"] = "over the wire";
            args["nozzleDiameterMm"] = 0.4;
            args["filamentDiameterMm"] = 1.75;
            args["beamDiameterMm"] = 0.05;
            args["bedWidthMm"] = 256.0;
            args["bedDepthMm"] = 257.0;
            args["bedHeightMm"] = 258.0;
            args["screenResolutionXPixels"] = 3840.0;
            args["screenResolutionYPixels"] = 2160.0;
            args["hasHeatedBed"] = true;
            args["wattageW"] = 350.0;
            args["idempotencyKey"] = "prn-proto-1";

            var result = await client.CallToolAsync("create_printer", args);
            Assert.True(result.IsError != true);

            using var doc = JsonDocument.Parse(RawText(result));
            var printer = doc.RootElement.GetProperty("printer");
            Assert.Equal("Bambu", printer.GetProperty("make").GetString());
            Assert.Equal("protocol-printer", printer.GetProperty("name").GetString());
            Assert.Equal("over the wire", printer.GetProperty("description").GetString());
            Assert.Equal("FFF", printer.GetProperty("categoryNickname").GetString());
            Assert.Equal(0.4, printer.GetProperty("nozzleDiameterMm").GetDouble());
            Assert.Equal(1.75, printer.GetProperty("filamentDiameterMm").GetDouble());
            Assert.Equal(0.05, printer.GetProperty("beamDiameterMm").GetDouble());
            Assert.Equal(256.0, printer.GetProperty("bedWidthMm").GetDouble());
            Assert.Equal(257.0, printer.GetProperty("bedDepthMm").GetDouble());
            Assert.Equal(258.0, printer.GetProperty("bedHeightMm").GetDouble());
            Assert.Equal(3840.0, printer.GetProperty("screenResolutionXPixels").GetDouble());
            Assert.Equal(2160.0, printer.GetProperty("screenResolutionYPixels").GetDouble());
            Assert.True(printer.GetProperty("hasHeatedBed").GetBoolean());
            Assert.Equal(350.0, printer.GetProperty("wattageW").GetDouble());
            Assert.True(printer.GetProperty("isActive").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("wasReplayed").GetBoolean());
        }

        [Fact]
        public async Task ReusedKeyWithDifferentArguments_SurfacesConflict()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            Dictionary<string, object?> Args(string make)
            {
                var a = BasicArgs("proto-conflict-printer");
                a["make"] = make;
                a["idempotencyKey"] = "prn-proto-conflict";
                return a;
            }

            var first = await client.CallToolAsync("create_printer", Args("Original"));
            Assert.True(first.IsError != true);

            var replay = await client.CallToolAsync("create_printer", Args("Original"));
            Assert.True(replay.IsError != true);
            using (var replayDoc = JsonDocument.Parse(RawText(replay)))
            {
                Assert.True(replayDoc.RootElement.GetProperty("wasReplayed").GetBoolean());
            }

            var conflict = await client.CallToolAsync("create_printer", Args("CHANGED"));
            Assert.True(conflict.IsError == true);
            Assert.StartsWith("conflict:", RawText(conflict));
        }

        [Fact]
        public async Task UpdatePrinter_OverTheWire_PatchesAndClears()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var args = BasicArgs("proto-update-printer");
            args["description"] = "to be cleared";
            args["wattageW"] = 100.0;
            var created = await client.CallToolAsync("create_printer", args);
            Assert.True(created.IsError != true);

            long id;
            using (var doc = JsonDocument.Parse(RawText(created)))
            {
                id = doc.RootElement.GetProperty("printer").GetProperty("id").GetInt64();
            }

            var updated = await client.CallToolAsync("update_printer", new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = "proto-update-renamed",
                ["clear"] = new[] { "description" },
            });
            Assert.True(updated.IsError != true);

            using var updatedDoc = JsonDocument.Parse(RawText(updated));
            Assert.Equal("proto-update-renamed", updatedDoc.RootElement.GetProperty("name").GetString());
            // A cleared field is ABSENT from the payload, not present-and-null: the SDK's serializer
            // omits nulls. That is how every unset field on this surface already reads, so an agent
            // sees "no description" either way — but the assertion has to match the real wire format.
            Assert.False(updatedDoc.RootElement.TryGetProperty("description", out _));
            // Untouched, and still there.
            Assert.Equal(100.0, updatedDoc.RootElement.GetProperty("wattageW").GetDouble());
        }

        // A typo in 'clear' must be rejected, never ignored: silently leaving a field set when the
        // caller believed it was cleared is a wrong answer.
        [Fact]
        public async Task UpdatePrinter_UnknownClearField_IsInvalidArguments()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var created = await client.CallToolAsync("create_printer", BasicArgs("proto-clear-typo"));
            long id;
            using (var doc = JsonDocument.Parse(RawText(created)))
            {
                id = doc.RootElement.GetProperty("printer").GetProperty("id").GetInt64();
            }

            var result = await client.CallToolAsync("update_printer", new Dictionary<string, object?>
            {
                ["id"] = id,
                ["clear"] = new[] { "nozzleDiameter" }, // real field is nozzleDiameterMm
            });
            Assert.True(result.IsError == true);
            Assert.StartsWith("invalid_arguments:", RawText(result));
        }

        [Fact]
        public async Task UpdatePrinter_ForeignPrinter_IsNotFound()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
            var result = await client.CallToolAsync("update_printer", new Dictionary<string, object?>
            {
                ["id"] = McpTestData.OtherPrinterId,
                ["name"] = "Hijacked",
            });
            Assert.True(result.IsError == true);
            Assert.StartsWith("not_found:", RawText(result));
        }
    }
}
