using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>Direct service-level tests for CreatePrintForMcp: creation, idempotent replay, ownership.</summary>
    public class CreatePrintServiceTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;

        public CreatePrintServiceTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private static IPrintService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IPrintService>();

        private static MaterialUsageInput Both(Guid id, double g, double estG) =>
            new(id, McpMeasurementSource.Weight, g, McpMeasurementSource.Weight, estG, null);

        /// <summary>Upserts a user setting for the primary test user (pattern from OctoprintControllerTests).</summary>
        private void SeedUserSetting(int userSettingTypeId, string value)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var existing = db.UserSettings
                .FirstOrDefault(u => u.UserId == IntegrationTestSeeder.TestUserId && u.UserSettingTypeId == userSettingTypeId);
            if (existing != null)
            {
                existing.Value = value;
            }
            else
            {
                db.UserSettings.Add(new UserSetting
                {
                    UserId = IntegrationTestSeeder.TestUserId,
                    UserSettingTypeId = userSettingTypeId,
                    Value = value,
                    CreatedById = IntegrationTestSeeder.TestUserId,
                    UpdatedById = IntegrationTestSeeder.TestUserId,
                    CreatedDate = DateTime.UtcNow,
                    UpdatedDate = DateTime.UtcNow,
                });
            }
            db.SaveChanges();
        }

        private void ClearUserSetting(int userSettingTypeId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var existing = db.UserSettings
                .Where(u => u.UserId == IntegrationTestSeeder.TestUserId && u.UserSettingTypeId == userSettingTypeId);
            db.UserSettings.RemoveRange(existing);
            db.SaveChanges();
        }

        [Fact]
        public async Task CreatePrintForMcp_CreatesPrint_AndIsIdempotent()
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPrintService>();
            var userId = IntegrationTestSeeder.TestUserId;
            var printerId = McpTestData.SearchPrinterId; // primary user's printer
            var usage = new List<MaterialUsageInput>
            {
                new(IntegrationTestSeeder.TestFilamentId1, McpMeasurementSource.Weight, 18.0, null, null, null),
            };

            var startedAt = DateTimeOffset.UtcNow;
            var first = await svc.CreatePrintForMcp(userId, "Benchy", printerId, Print.PrintStatus.Success,
                startedAt, 3600, null, "note", null, null, null, null, null, null, usage, "svc-key-1", CancellationToken.None);
            var second = await svc.CreatePrintForMcp(userId, "Benchy", printerId, Print.PrintStatus.Success,
                startedAt, 3600, null, "note", null, null, null, null, null, null, usage, "svc-key-1", CancellationToken.None);

            Assert.False(first.WasReplayed);
            Assert.True(second.WasReplayed);
            Assert.Equal(first.Print.Id, second.Print.Id);
            Assert.Contains(first.MaterialRemaining, m => m.MaterialId == IntegrationTestSeeder.TestFilamentId1);
        }

        [Fact]
        public async Task CreatePrintForMcp_ForeignPrinter_Throws()
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPrintService>();

            await Assert.ThrowsAsync<McpToolException>(() => svc.CreatePrintForMcp(
                IntegrationTestSeeder.TestUserId, "x", McpTestData.OtherPrinterId, Print.PrintStatus.Success,
                null, null, null, null, null, null, null, null, null, null,
                new List<MaterialUsageInput>(), "svc-key-foreign", CancellationToken.None));
        }

        [Fact]
        public async Task Create_PersistsNewFields_ReadBack()
        {
            using var scope = _factory.Services.CreateScope();
            var r = await Svc(scope).CreatePrintForMcp(
                IntegrationTestSeeder.TestUserId, "Benchy", McpTestData.SearchPrinterId, Print.PrintStatus.Success,
                DateTimeOffset.UtcNow, 3600, 3300, "note", null, "benchy.gcode", "https://example.com/b",
                Print.PrintViewStatus.Unlisted, true, false,
                new List<MaterialUsageInput> { Both(IntegrationTestSeeder.TestFilamentId1, 18.0, 17.0) },
                "ck-fields", CancellationToken.None);

            Assert.False(r.WasReplayed);
            Assert.Equal("benchy.gcode", r.Print.FileName);
            Assert.Equal("https://example.com/b", r.Print.Url);
            Assert.Equal("Unlisted", r.Print.ViewStatus);
            Assert.Equal(3300, r.Print.EstimatedDurationSeconds);
            Assert.True(r.Print.AllowComments);
            Assert.False(r.Print.AllowFileDownloads);
            var row = Assert.Single(r.Print.MaterialsUsed);
            Assert.Equal(18.0, row.ActualGrams);
            Assert.Equal(17.0, row.EstimatedGrams);
        }

        [Fact]
        public async Task Create_SameKeyDifferentPayload_Conflicts()
        {
            using var scope = _factory.Services.CreateScope();
            await Svc(scope).CreatePrintForMcp(IntegrationTestSeeder.TestUserId, "A", McpTestData.SearchPrinterId,
                Print.PrintStatus.Success, null, 3600, null, null, null, null, null, null, null, null,
                new List<MaterialUsageInput>(), "ck-conflict", CancellationToken.None);

            var ex = await Assert.ThrowsAsync<McpToolException>(() => Svc(scope).CreatePrintForMcp(
                IntegrationTestSeeder.TestUserId, "DIFFERENT", McpTestData.SearchPrinterId, Print.PrintStatus.Success,
                null, 3600, null, null, null, null, null, null, null, null,
                new List<MaterialUsageInput>(), "ck-conflict", CancellationToken.None));
            Assert.Equal("conflict", ex.Code);
        }

        [Fact]
        public async Task Create_NullFingerprintRecord_ReplaysWithoutComparison()
        {
            // A legacy record (RequestFingerprint == null) must replay rather than conflict: there is
            // no stored payload to compare against, so a comparison would be a guess.
            long printId;
            using (var seed = _factory.Services.CreateScope())
            {
                var db = seed.ServiceProvider.GetRequiredService<PrintLogContext>();
                var p = new Print
                {
                    Title = "legacy",
                    PrinterId = McpTestData.SearchPrinterId,
                    Status = Print.PrintStatus.Success,
                    CreatedById = IntegrationTestSeeder.TestUserId,
                    UpdatedById = IntegrationTestSeeder.TestUserId,
                };
                db.Prints.Add(p);
                await db.SaveChangesAsync();
                printId = p.Id;
                db.McpIdempotencyRecords.Add(new McpIdempotencyRecord
                {
                    UserId = IntegrationTestSeeder.TestUserId,
                    ToolName = "create_print",
                    IdempotencyKey = "ck-legacy",
                    RequestFingerprint = null,
                    CreatedPrintId = printId,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            using var scope = _factory.Services.CreateScope();
            var r = await Svc(scope).CreatePrintForMcp(IntegrationTestSeeder.TestUserId, "totally different",
                McpTestData.SearchPrinterId, Print.PrintStatus.Failed, null, 999, null, null, null, null, null, null, null, null,
                new List<MaterialUsageInput>(), "ck-legacy", CancellationToken.None);
            Assert.True(r.WasReplayed);
            Assert.Equal(printId, r.Print.Id);
        }

        [Fact]
        public async Task Create_DefaultsToPrivate_WhenNoSettings()
        {
            ClearUserSetting(1);
            ClearUserSetting(3);
            using var scope = _factory.Services.CreateScope();
            var r = await Svc(scope).CreatePrintForMcp(IntegrationTestSeeder.TestUserId, "def", McpTestData.SearchPrinterId,
                Print.PrintStatus.Success, null, null, null, null, null, null, null, null, null, null,
                new List<MaterialUsageInput>(), "ck-def", CancellationToken.None);
            Assert.Equal("Private", r.Print.ViewStatus);
            Assert.False(r.Print.AllowComments);
            Assert.False(r.Print.AllowFileDownloads);
        }

        [Fact]
        public async Task Create_HonorsUserSettings_WhenPresent()
        {
            SeedUserSetting(1, "Public");
            SeedUserSetting(3, "true");
            try
            {
                using var scope = _factory.Services.CreateScope();
                var r = await Svc(scope).CreatePrintForMcp(IntegrationTestSeeder.TestUserId, "settings",
                    McpTestData.SearchPrinterId, Print.PrintStatus.Success, null, null, null, null, null, null, null,
                    null, null, null, new List<MaterialUsageInput>(), "ck-settings", CancellationToken.None);
                Assert.Equal("Public", r.Print.ViewStatus);
                Assert.True(r.Print.AllowComments);
            }
            finally
            {
                ClearUserSetting(1);
                ClearUserSetting(3);
            }
        }

        [Fact]
        public async Task Create_MalformedUserSettings_FallBackToSafeDefaults()
        {
            // "999" parses as a PrintViewStatus numerically but is not a DEFINED member; "maybe" is not
            // a bool. Both must fall back rather than persist a nonsense visibility.
            SeedUserSetting(1, "999");
            SeedUserSetting(3, "maybe");
            try
            {
                using var scope = _factory.Services.CreateScope();
                var r = await Svc(scope).CreatePrintForMcp(IntegrationTestSeeder.TestUserId, "malformed",
                    McpTestData.SearchPrinterId, Print.PrintStatus.Success, null, null, null, null, null, null, null,
                    null, null, null, new List<MaterialUsageInput>(), "ck-malformed", CancellationToken.None);
                Assert.Equal("Private", r.Print.ViewStatus);
                Assert.False(r.Print.AllowComments);
            }
            finally
            {
                ClearUserSetting(1);
                ClearUserSetting(3);
            }
        }

        [Fact]
        public async Task Create_VolumeOnResin_Ok_LengthOnResin_Invalid()
        {
            using var scope = _factory.Services.CreateScope();
            // Volume needs only density -> resin (no diameter) is valid.
            var ok = await Svc(scope).CreatePrintForMcp(IntegrationTestSeeder.TestUserId, "vol", McpTestData.SearchPrinterId,
                Print.PrintStatus.Success, null, null, null, null, null, null, null, null, null, null,
                new List<MaterialUsageInput> { new(McpTestData.ResinMaterialId, McpMeasurementSource.Volume, 10.0, null, null, null) },
                "ck-vol", CancellationToken.None);
            Assert.False(ok.WasReplayed);

            var ex = await Assert.ThrowsAsync<McpToolException>(() => Svc(scope).CreatePrintForMcp(
                IntegrationTestSeeder.TestUserId, "len", McpTestData.SearchPrinterId, Print.PrintStatus.Success,
                null, null, null, null, null, null, null, null, null, null,
                new List<MaterialUsageInput> { new(McpTestData.ResinMaterialId, McpMeasurementSource.Length, 100.0, null, null, null) },
                "ck-len", CancellationToken.None));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Create_WeightConversion_IsExact()
        {
            using var scope = _factory.Services.CreateScope();
            var r = await Svc(scope).CreatePrintForMcp(IntegrationTestSeeder.TestUserId, "w", McpTestData.SearchPrinterId,
                Print.PrintStatus.Success, null, null, null, null, null, null, null, null, null, null,
                new List<MaterialUsageInput> { new(IntegrationTestSeeder.TestFilamentId1, McpMeasurementSource.Weight, 12.5, null, null, null) },
                "ck-exact", CancellationToken.None);
            Assert.Equal(12.5, Assert.Single(r.Print.MaterialsUsed).ActualGrams);
        }

        [Fact]
        public async Task Create_OverflowingAmount_IsRejected()
        {
            using var scope = _factory.Services.CreateScope();
            // 2,000,000 ml of density-1.1 resin is 2.2e9 mg: inside the MaxAmountMagnitude input cap,
            // but beyond what the int milligram column holds. Must be rejected, not truncated.
            var ex = await Assert.ThrowsAsync<McpToolException>(() => Svc(scope).CreatePrintForMcp(
                IntegrationTestSeeder.TestUserId, "overflow", McpTestData.SearchPrinterId, Print.PrintStatus.Success,
                null, null, null, null, null, null, null, null, null, null,
                new List<MaterialUsageInput> { new(McpTestData.ResinMaterialId, McpMeasurementSource.Volume, 2_000_000.0, null, null, null) },
                "ck-overflow", CancellationToken.None));
            Assert.Equal("invalid_arguments", ex.Code);
        }
    }
}
