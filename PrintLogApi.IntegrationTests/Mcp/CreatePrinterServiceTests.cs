using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    public class CreatePrinterServiceTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;
        public CreatePrinterServiceTests(McpDataWebApplicationFactory factory) => _factory = factory;
        private static IPrinterService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IPrinterService>();

        private static PrinterAttributesInput Basic(string name = "Test Printer") => new()
        {
            Make = "Bambu",
            Model = "X1C",
            Name = name,
        };

        private static Task<CreatePrinterResult> Create(IServiceScope s, PrinterAttributesInput input, string? key = null) =>
            Svc(s).CreatePrinterForMcp(IntegrationTestSeeder.TestUserId, input, key, CancellationToken.None);

        [Fact]
        public async Task Create_RoundTripsEverySettableField()
        {
            using var scope = _factory.Services.CreateScope();
            var result = await Create(scope, Basic("Rich Printer") with
            {
                Description = "the workshop machine",
                CategoryNickname = "FFF",
                NozzleDiameterMm = 0.4,
                FilamentDiameterMm = 1.75,
                BeamDiameterMm = 0.05,
                BedWidthMm = 256,
                BedDepthMm = 257,
                BedHeightMm = 258,
                ScreenResolutionXPixels = 3840,
                ScreenResolutionYPixels = 2160,
                HasHeatedBed = true,
                HasHeatedChamber = false,
                WattageW = 350,
            });

            var p = result.Printer;
            Assert.False(result.WasReplayed);
            Assert.Equal("Bambu", p.Make);
            Assert.Equal("X1C", p.Model);
            Assert.Equal("Rich Printer", p.Name);
            Assert.Equal("the workshop machine", p.Description);
            Assert.Equal("FFF", p.CategoryNickname);
            Assert.Equal(0.4, p.NozzleDiameterMm);
            Assert.Equal(1.75, p.FilamentDiameterMm);
            Assert.Equal(0.05, p.BeamDiameterMm);
            // Three distinct bed values: identical ones would pass even if the projection swapped them.
            Assert.Equal(256, p.BedWidthMm);
            Assert.Equal(257, p.BedDepthMm);
            Assert.Equal(258, p.BedHeightMm);
            Assert.Equal(3840, p.ScreenResolutionXPixels);
            Assert.Equal(2160, p.ScreenResolutionYPixels);
            Assert.True(p.HasHeatedBed);
            Assert.False(p.HasHeatedChamber);
            Assert.Equal(350, p.WattageW);
        }

        // MCP-only semantic: the website DTO's IsActive is a non-nullable bool that defaults false,
        // but a freshly created printer is presumably in use.
        [Fact]
        public async Task Create_DefaultsIsActiveToTrue()
        {
            using var scope = _factory.Services.CreateScope();
            var result = await Create(scope, Basic("Active Default"));
            Assert.True(result.Printer.IsActive);
        }

        [Fact]
        public async Task Create_HonoursAnExplicitInactive()
        {
            using var scope = _factory.Services.CreateScope();
            var result = await Create(scope, Basic("Explicit Inactive") with { IsActive = false });
            Assert.False(result.Printer.IsActive);
        }

        [Fact]
        public async Task Create_OmittedCategory_ResolvesTheDefault()
        {
            using var scope = _factory.Services.CreateScope();
            var result = await Create(scope, Basic("Default Category"));
            Assert.Equal(PrinterService.DefaultPrinterCategoryNickname, result.Printer.CategoryNickname);
        }

        [Fact]
        public async Task Create_KnownCategory_IsUsed()
        {
            using var scope = _factory.Services.CreateScope();
            var result = await Create(scope, Basic("SLA Printer") with { CategoryNickname = "SLA" });
            Assert.Equal("SLA", result.Printer.CategoryNickname);
        }

        // An unknown category is rejected, never silently replaced with the default: a printer filed
        // under a category the caller did not ask for is a wrong answer that reads like a right one.
        [Fact]
        public async Task Create_UnknownCategory_IsRejected()
        {
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Create(scope, Basic("Bad Category") with { CategoryNickname = "NOT-A-CATEGORY" }));
            Assert.Equal("invalid_arguments", ex.Code);
            Assert.Contains("NOT-A-CATEGORY", ex.Message);
        }

        // There is no tool that lists printer categories, so a rejection that does not name the valid
        // options leaves an agent guessing. The set is a small fixed seed, not per-user, so the error
        // can simply carry it.
        [Fact]
        public async Task Create_UnknownCategory_ErrorNamesTheValidCategories()
        {
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Create(scope, Basic("Enumerate Me") with { CategoryNickname = "ResinPrinter" }));

            Assert.Contains("FFF", ex.Message);
            Assert.Contains("SLA", ex.Message);
            Assert.Contains("FDM", ex.Message);
        }

        [Fact]
        public async Task Create_SetsTheCallerAsOwner()
        {
            using var scope = _factory.Services.CreateScope();
            var result = await Create(scope, Basic("Owned Printer"));

            var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var stored = await ctx.Printers.AsNoTracking().SingleAsync(p => p.Id == result.Printer.Id);
            Assert.Equal(IntegrationTestSeeder.TestUserId, stored.UserId);
        }

        // The whole point of the write surface: nothing it does may touch loaded state.
        [Fact]
        public async Task Create_AddsNoPrinterFilamentRows()
        {
            using var scope = _factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var before = await ctx.Set<PrinterFilament>().CountAsync();

            var result = await Create(scope, Basic("No Spools"));

            Assert.Equal(before, await ctx.Set<PrinterFilament>().CountAsync());
            Assert.Empty(result.Printer.LoadedFilaments);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public async Task Create_BlankOrMissingName_IsRejected(string name)
        {
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Create(scope, Basic() with { Name = name }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public async Task Create_BlankOrMissingMake_IsRejected(string make)
        {
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Create(scope, Basic() with { Make = make }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public async Task Create_BlankOrMissingModel_IsRejected(string model)
        {
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Create(scope, Basic() with { Model = model }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Create_NegativeNumeric_IsRejected()
        {
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Create(scope, Basic() with { WattageW = -1 }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Create_TrimsStrings()
        {
            using var scope = _factory.Services.CreateScope();
            var result = await Create(scope, new PrinterAttributesInput
            {
                Make = "  Prusa  ",
                Model = "  MK4  ",
                Name = "  Trimmed Printer  ",
                Description = "  d  ",
            });
            Assert.Equal("Prusa", result.Printer.Make);
            Assert.Equal("MK4", result.Printer.Model);
            Assert.Equal("Trimmed Printer", result.Printer.Name);
            Assert.Equal("d", result.Printer.Description);
        }

        // Without a key, a retry is a SECOND printer. Stated in the tool description; asserted here
        // so the residual at-least-once risk is a documented property, not an accident.
        [Fact]
        public async Task Create_WithoutKey_CreatesASecondPrinter()
        {
            using var scope = _factory.Services.CreateScope();
            var first = await Create(scope, Basic("Duplicate Me"));
            var second = await Create(scope, Basic("Duplicate Me"));
            Assert.NotEqual(first.Printer.Id, second.Printer.Id);
            Assert.False(second.WasReplayed);
        }

        [Fact]
        public async Task Create_SameKeyAndArguments_Replays()
        {
            using var scope = _factory.Services.CreateScope();
            var first = await Create(scope, Basic("Replay Me"), "prn-key-1");
            var replay = await Create(scope, Basic("Replay Me"), "prn-key-1");

            Assert.True(replay.WasReplayed);
            Assert.Equal(first.Printer.Id, replay.Printer.Id);

            var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            Assert.Equal(1, await ctx.Printers.CountAsync(p => p.Name == "Replay Me"));
        }

        // Replaying would silently discard the new arguments, so a reused key with a different
        // payload is a caller bug, not a retry.
        [Fact]
        public async Task Create_SameKeyDifferentArguments_Conflicts()
        {
            using var scope = _factory.Services.CreateScope();
            await Create(scope, Basic("Conflict Me"), "prn-key-2");
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Create(scope, Basic("Conflict Me") with { Make = "CHANGED" }, "prn-key-2"));
            Assert.Equal("conflict", ex.Code);
        }

        [Fact]
        public async Task Create_KeyIsScopedToTheTool_NotSharedWithOtherCreateTools()
        {
            using var scope = _factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            // A create_material record with the SAME user and key must not be seen by create_printer.
            ctx.McpIdempotencyRecords.Add(McpIdempotencyRecordFactory.ForMaterial(
                IntegrationTestSeeder.TestUserId, "shared-key", "some-fingerprint", McpTestData.ResinMaterialId));
            await ctx.SaveChangesAsync();

            var result = await Create(scope, Basic("Tool Scoped"), "shared-key");
            Assert.False(result.WasReplayed);
        }

        // A legacy record predating the fingerprint column has no payload to compare against.
        [Fact]
        public async Task Create_NullFingerprintRecord_ReplaysWithoutComparison()
        {
            using var scope = _factory.Services.CreateScope();
            var seeded = await Create(scope, Basic("Legacy Target"));

            var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            ctx.McpIdempotencyRecords.Add(McpIdempotencyRecordFactory.ForPrinter(
                IntegrationTestSeeder.TestUserId, "prn-legacy", null, seeded.Printer.Id));
            await ctx.SaveChangesAsync();

            var replay = await Create(scope, Basic("Totally Different Args") with { Make = "Nope" }, "prn-legacy");
            Assert.True(replay.WasReplayed);
            Assert.Equal(seeded.Printer.Id, replay.Printer.Id);
        }

        // A record pointing at a printer that no longer exists (or was never a printer target) is
        // dangling: report it rather than inventing a result.
        [Fact]
        public async Task Create_DanglingRecord_IsNotFound()
        {
            using var scope = _factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            ctx.McpIdempotencyRecords.Add(McpIdempotencyRecordFactory.ForPrinter(
                IntegrationTestSeeder.TestUserId, "prn-dangling", null, printerId: 999_999));
            await ctx.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Create(scope, Basic("Dangling"), "prn-dangling"));
            Assert.Equal("not_found", ex.Code);
        }

        // Another user's printer behind the caller's key must not be readable through a replay.
        [Fact]
        public async Task Create_RecordPointingAtAForeignPrinter_IsNotFound()
        {
            using var scope = _factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            ctx.McpIdempotencyRecords.Add(McpIdempotencyRecordFactory.ForPrinter(
                IntegrationTestSeeder.TestUserId, "prn-foreign", null, McpTestData.OtherPrinterId));
            await ctx.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Create(scope, Basic("Foreign"), "prn-foreign"));
            Assert.Equal("not_found", ex.Code);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Create_BlankKey_IsRejected(string key)
        {
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(() => Create(scope, Basic("Blank Key"), key));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Create_OverLongKey_IsRejected()
        {
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Create(scope, Basic("Long Key"), new string('k', 201)));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Create_BumpsCacheVersion()
        {
            var cache = _factory.Services.GetRequiredService<ICacheVersionService>();
            using var scope = _factory.Services.CreateScope();
            var before = cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId);
            await Create(scope, Basic("Cache Bump Printer"));
            Assert.NotEqual(before, cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId));
        }

        // A replay writes nothing, so invalidating would throw away a warm cache for no reason.
        [Fact]
        public async Task Create_Replay_DoesNotBumpCacheVersion()
        {
            var cache = _factory.Services.GetRequiredService<ICacheVersionService>();
            using var scope = _factory.Services.CreateScope();
            await Create(scope, Basic("Replay Cache Printer"), "prn-key-3");
            var afterFirst = cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId);
            var replay = await Create(scope, Basic("Replay Cache Printer"), "prn-key-3");
            Assert.True(replay.WasReplayed);
            Assert.Equal(afterFirst, cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId));
        }
    }
}
