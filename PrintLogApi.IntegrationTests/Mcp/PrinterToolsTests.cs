using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// Covers list_printers and get_printer. Before these tools existed a printer's name only ever
    /// leaked out embedded in a print result, so an agent could not resolve "my Tevo" to an id, and
    /// nothing could answer "what is loaded on it right now".
    /// </summary>
    public class PrinterToolsTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private const string ListToolName = "list_printers";
        private const string GetToolName = "get_printer";

        private static readonly JsonSerializerOptions JsonOptions =
            new() { PropertyNameCaseInsensitive = true };

        private readonly McpDataWebApplicationFactory _factory;

        public PrinterToolsTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private sealed record PrinterListItem(
            long Id, string Name, string? Make, string? Model, double? NozzleDiameterMm, bool IsActive);

        private sealed record PageResult(
            List<PrinterListItem> Items, int Page, int PageSize, int TotalCount, int TotalPages);

        private sealed record LoadedFilament(
            Guid FilamentId, string? Name, string? Brand, string? Material, string? Color,
            double? DiameterMm, double RemainingGrams, DateTimeOffset LoadedAt);

        private sealed record PrinterDetail(
            long Id, string Name, string? Make, string? Model, string? Description,
            string? CategoryNickname, double? NozzleDiameterMm,
            double? BedWidthMm, double? BedDepthMm, double? BedHeightMm,
            bool? HasHeatedBed, bool? HasHeatedChamber, double? WattageW, bool IsActive,
            List<LoadedFilament> LoadedFilaments, int LoadedFilamentCount,
            bool LoadedFilamentsTruncated, int ExcludedUnreadableSpools,
            double? FilamentDiameterMm, double? BeamDiameterMm,
            double? ScreenResolutionXPixels, double? ScreenResolutionYPixels);

        private static T Parse<T>(CallToolResult result)
        {
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
        }

        private static async Task<PageResult> List(McpClient client, Dictionary<string, object?> args) =>
            Parse<PageResult>(await client.CallToolAsync(ListToolName, args));

        private static async Task<PrinterDetail> Get(McpClient client, long id) =>
            Parse<PrinterDetail>(await client.CallToolAsync(
                GetToolName, new Dictionary<string, object?> { ["id"] = id }));

        [Fact]
        public async Task ListPrinters_ReturnsOnlyCallersPrinters()
        {
            await using var client = await _factory.ConnectAsync();
            var page = await List(client, new() { ["pageSize"] = 100 });

            Assert.DoesNotContain(page.Items, p => p.Id == McpTestData.OtherPrinterId);
            Assert.Contains(page.Items, p => p.Id == McpTestData.SearchPrinterId);
            Assert.Equal(page.Items.Count, page.TotalCount);
        }

        [Fact]
        public async Task ListPrinters_IsPaginated()
        {
            await using var client = await _factory.ConnectAsync();
            var page = await List(client, new() { ["pageSize"] = 1 });

            Assert.Single(page.Items);
            Assert.True(page.TotalCount > 1);
            Assert.Equal(page.TotalCount, page.TotalPages); // pageSize 1
        }

        [Fact]
        public async Task ListPrinters_OwnerIsolation_OtherUserSeesOnlyOwnPrinter()
        {
            await using var client = await _factory.ConnectAsync(McpTestData.OtherUserOAuthId);
            var page = await List(client, new() { ["pageSize"] = 100 });

            var printer = Assert.Single(page.Items);
            Assert.Equal(McpTestData.OtherPrinterId, printer.Id);
        }

        [Fact]
        public async Task GetPrinter_ReturnsOwnPrinterDetail()
        {
            await using var client = await _factory.ConnectAsync();
            var printer = await Get(client, McpTestData.SearchPrinterId);

            Assert.Equal(McpTestData.SearchPrinterId, printer.Id);
            Assert.Equal("Search Fixture Printer", printer.Name);
            Assert.Equal("Fixture", printer.Make);
        }

        [Fact]
        public async Task GetPrinter_ForeignId_IsNotFound()
        {
            // Creator-only, and a foreign id must not be an existence oracle: another user's
            // printer is indistinguishable from one that does not exist.
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(
                client, GetToolName, new() { ["id"] = McpTestData.OtherPrinterId }));
        }

        [Fact]
        public async Task LoadedFilaments_ExcludeUnloadedRows()
        {
            // PrinterFilament keeps history. Without the UnloadedDateTime filter, every spool ever
            // mounted would read as "loaded right now" - a wrong answer, confidently given.
            await using var client = await _factory.ConnectAsync();
            var printer = await Get(client, McpTestData.SearchPrinterId);

            var loaded = Assert.Single(printer.LoadedFilaments);
            Assert.Equal("Long PLA", loaded.Name);          // currently loaded
            Assert.DoesNotContain(printer.LoadedFilaments, f => f.Name == "Plus PLA"); // unloaded
            Assert.False(printer.LoadedFilamentsTruncated);
            Assert.Equal(1, printer.LoadedFilamentCount);
        }

        [Fact]
        public async Task LoadedFilaments_ForeignSpool_IsExcludedAndCounted()
        {
            // A corrupt row points at another user's spool. Excluding it silently would hide the
            // fact that something IS loaded; leaking it would expose that user's material/colour.
            await using var client = await _factory.ConnectAsync();
            var printer = await Get(client, McpTestData.SearchPrinterId);

            Assert.DoesNotContain(printer.LoadedFilaments, f => f.Name == "OTHER USER SPOOL");
            Assert.DoesNotContain(printer.LoadedFilaments, f => f.Color == "Secret Purple");
            Assert.Equal(1, printer.ExcludedUnreadableSpools);
        }

        [Fact]
        public async Task LoadedFilaments_BeyondCap_SetTruncationFlagAndTrueCount()
        {
            await using var client = await _factory.ConnectAsync();
            var printer = await Get(client, McpTestData.AmsPrinterId);

            Assert.Equal(10, printer.LoadedFilaments.Count);                       // the cap
            Assert.Equal(McpTestData.AmsLoadedSpoolCount, printer.LoadedFilamentCount); // true count: 12
            Assert.True(printer.LoadedFilamentsTruncated);
            Assert.Equal(0, printer.ExcludedUnreadableSpools);
        }

        [Fact]
        public async Task LoadedFilaments_CarryMaterialColorAndRemainingGrams()
        {
            await using var client = await _factory.ConnectAsync();
            var printer = await Get(client, McpTestData.SearchPrinterId);

            var loaded = Assert.Single(printer.LoadedFilaments);
            Assert.Equal("PLA (Polylactic Acid)", loaded.Material);
            Assert.Equal("Light Blue", loaded.Color);
            Assert.Equal(1.75, loaded.DiameterMm);
            // 1,000,000 mg initial, no usage or adjustment against this spool.
            Assert.Equal(1000.0, loaded.RemainingGrams);
        }

        /// <summary>
        /// Each newly-exposed field is asserted independently, with a distinct value:
        /// PrinterDetailResult is a 22-field positional record, so two same-typed fields swapped in
        /// the constructor would still compile and would still pass a test that checked only one.
        /// </summary>
        [Fact]
        public async Task GetPrinter_ReturnsTheNewlyExposedSettableFields()
        {
            long printerId;
            using (var scope = _factory.Services.CreateScope())
            {
                var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                var printer = new Printer
                {
                    Name = "Detail Field Printer",
                    Make = "Fixture",
                    Model = "DF1",
                    UserId = IntegrationTestSeeder.TestUserId,
                    IsActive = true,
                    FilamentDiameter = 2.85,
                    BeamDiameter = 0.05,
                    ScreenResolutionXPixels = 3840,
                    ScreenResolutionYPixels = 2160,
                };
                ctx.Printers.Add(printer);
                await ctx.SaveChangesAsync();
                printerId = printer.Id;
            }

            await using var client = await _factory.ConnectAsync();
            var detail = await Get(client, printerId);

            Assert.Equal(2.85, detail.FilamentDiameterMm);
            Assert.Equal(0.05, detail.BeamDiameterMm);
            Assert.Equal(3840, detail.ScreenResolutionXPixels);
            Assert.Equal(2160, detail.ScreenResolutionYPixels);
        }
    }
}
