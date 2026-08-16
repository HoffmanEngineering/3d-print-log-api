using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    public class UpdatePrintServiceTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;
        public UpdatePrintServiceTests(McpDataWebApplicationFactory factory) => _factory = factory;
        private IPrintService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IPrintService>();

        private async Task<long> Seed(IServiceScope scope, string key) =>
            (await Svc(scope).CreatePrintForMcp(IntegrationTestSeeder.TestUserId, "Orig", McpTestData.SearchPrinterId,
                Print.PrintStatus.Success, DateTimeOffset.UtcNow, 3600, null, "orig notes", null, "orig.gcode", null,
                null, null, null, new List<MaterialUsageInput>(), key, CancellationToken.None)).Print.Id;

        private static ISet<string> Clear(params string[] n) => new HashSet<string>(n);
        private static readonly ISet<string> None = new HashSet<string>();

        [Fact]
        public async Task Update_SetsTitleStartedAt_LeavesFileName()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "u-1");
            // Millisecond precision: the stored column does not keep sub-millisecond ticks, so a raw
            // UtcNow would differ on read-back for reasons that have nothing to do with the update.
            var when = new DateTimeOffset(
                DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() * TimeSpan.TicksPerMillisecond
                + DateTimeOffset.UnixEpoch.Ticks, TimeSpan.Zero);
            var r = await Svc(scope).UpdateOwnPrintForMcp(IntegrationTestSeeder.TestUserId, id, "New", null, null, when,
                null, null, null, null, null, null, null, null, null, false, null, None, CancellationToken.None);
            Assert.Equal("New", r.Title);
            Assert.Equal(when, r.StartedAt);
            Assert.Equal("orig.gcode", r.FileName);
        }

        [Fact]
        public async Task Update_ClearsFileNameAndStartedAt()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "u-2");
            var r = await Svc(scope).UpdateOwnPrintForMcp(IntegrationTestSeeder.TestUserId, id, null, null, null, null,
                null, null, null, null, null, null, null, null, null, false, null, Clear("fileName", "startedAt"), CancellationToken.None);
            Assert.Null(r.FileName);
            Assert.Null(r.StartedAt);
        }

        [Fact]
        public async Task Update_ForeignPrinter_NotFound_AndPreservesOriginal()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "u-3");
            await Assert.ThrowsAsync<McpToolException>(() => Svc(scope).UpdateOwnPrintForMcp(
                IntegrationTestSeeder.TestUserId, id, "SHOULD NOT STICK", null, null, null, McpTestData.OtherPrinterId,
                null, null, null, null, null, null, null, null, false, null, None, CancellationToken.None));
            // Re-read: nothing changed (validate-before-mutate).
            var still = await Svc(scope).GetOwnPrintDetailForMcp(IntegrationTestSeeder.TestUserId, id, CancellationToken.None);
            Assert.Equal("Orig", still!.Title);
            Assert.Equal(McpTestData.SearchPrinterId, still.PrinterId);
        }

        [Fact]
        public async Task Update_BumpsCacheVersion_OnSuccess_NotOnRejection()
        {
            var cache = _factory.Services.GetRequiredService<ICacheVersionService>();
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "u-cache");

            // A rejected update mutates nothing, so it must not invalidate either.
            var beforeRejected = cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId);
            await Assert.ThrowsAsync<McpToolException>(() => Svc(scope).UpdateOwnPrintForMcp(
                IntegrationTestSeeder.TestUserId, id, null, null, null, null, McpTestData.OtherPrinterId,
                null, null, null, null, null, null, null, null, false, null, None, CancellationToken.None));
            Assert.Equal(beforeRejected, cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId));

            // A successful update touches summary-affecting fields, so cached summaries must be dropped.
            var before = cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId);
            await Svc(scope).UpdateOwnPrintForMcp(IntegrationTestSeeder.TestUserId, id, "cache", Print.PrintStatus.Failed,
                null, null, null, 1200, null, null, null, null, null, null, null, true,
                new List<MaterialUsageInput> { new(IntegrationTestSeeder.TestFilamentId1, McpMeasurementSource.Weight, 5.0, null, null, null) },
                None, CancellationToken.None);
            Assert.NotEqual(before, cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId));
        }

        [Fact]
        public async Task Update_SetAndClearSameField_Invalid()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "u-4");
            var ex = await Assert.ThrowsAsync<McpToolException>(() => Svc(scope).UpdateOwnPrintForMcp(
                IntegrationTestSeeder.TestUserId, id, null, null, null, null, null, null, null, "new.gcode", null,
                null, null, null, null, false, null, Clear("fileName"), CancellationToken.None));
            Assert.Equal("invalid_arguments", ex.Code);
        }
    }
}
