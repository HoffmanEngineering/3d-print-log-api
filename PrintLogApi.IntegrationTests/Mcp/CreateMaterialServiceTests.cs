using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Enums;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    public class CreateMaterialServiceTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;
        public CreateMaterialServiceTests(McpDataWebApplicationFactory factory) => _factory = factory;
        private IFilamentService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IFilamentService>();

        private static MaterialAttributesInput Basic(string name = "Test Spool") => new()
        {
            DisplayName = name,
            MaterialType = "PLA",
            MaterialCategoryNickname = "filament",
            DensityGramPerCubicCm = 1.24,
            DiameterMm = 1.75,
            Source = McpMeasurementSource.Weight,
            InitialAmount = 1000,
        };

        private Task<CreateMaterialResult> Create(IServiceScope s, MaterialAttributesInput input, string key = null) =>
            Svc(s).CreateMaterialForMcp(IntegrationTestSeeder.TestUserId, input, key, CancellationToken.None);

        [Fact]
        public async Task Create_RoundTripsEveryEnrichedField()
        {
            using var scope = _factory.Services.CreateScope();
            var purchased = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
            var result = await Create(scope, Basic("Rich Spool") with
            {
                Brand = "Acme",
                ColorName = "Sunset",
                Colors = new[] { "FF8800", "FFAA00" },
                ColorPattern = ColorPatternType.Gradient,
                FinishType = FilamentFinishType.Silk,
                Effects = new[] { FilamentEffect.Sparkle },
                StorageLocation = "Shelf B",
                Notes = "a note",
                IsFavorite = true,
                SpoolWeightGrams = 220,
                InitialTotalWeightGrams = 1220,
                TempRangeStartC = 190,
                TempRangeEndC = 220,
                RecommendedTempC = 205,
                RecommendedBedTempC = 60,
                MeltingTemperatureC = 160,
                PurchaseDate = purchased,
                PurchaseLocation = "acme.example",
                PurchasePriceValue = "24.99",
                PurchasePriceCurrency = "USD",
                PurchaseNotes = "on sale",
            });

            var m = result.Material;
            Assert.False(result.WasReplayed);
            Assert.Equal("Acme", m.Brand);
            Assert.Equal("Sunset", m.ColorName);
            Assert.Equal(new[] { "FF8800", "FFAA00" }, m.Colors);
            Assert.Equal("FF8800", m.ColorHex); // Colors[0] is authoritative
            Assert.Equal("Gradient", m.ColorPattern);
            Assert.Equal("Silk", m.FinishType);
            Assert.Equal(new[] { "Sparkle" }, m.Effects);
            Assert.Equal("Shelf B", m.StorageLocation);
            Assert.Equal("a note", m.Notes);
            Assert.True(m.IsFavorite);
            Assert.Equal(220d, m.SpoolWeightGrams);
            Assert.Equal(1220d, m.InitialTotalWeightGrams);
            Assert.Equal(190d, m.TempRangeStartC);
            Assert.Equal(220d, m.TempRangeEndC);
            Assert.Equal(205d, m.RecommendedTempC);
            Assert.Equal(60d, m.RecommendedBedTempC);
            Assert.Equal(160d, m.MeltingTemperatureC);
            Assert.Equal(purchased, m.PurchaseDate);
            Assert.Equal("acme.example", m.PurchaseLocation);
            Assert.Equal("24.99", m.PurchasePriceValue);
            Assert.Equal("USD", m.PurchasePriceCurrency);
            Assert.Equal("on sale", m.PurchaseNotes);
            Assert.Equal("Weight", m.SourceUnit);
            Assert.Equal(1000d, m.InitialAmountInSourceUnit);
            Assert.True(m.HasNominalCapacity);
        }

        [Fact]
        public async Task Create_ResinFields_RoundTrip()
        {
            using var scope = _factory.Services.CreateScope();
            var result = await Create(scope, new MaterialAttributesInput
            {
                DisplayName = "Resin Bottle",
                MaterialType = "Standard Resin",
                MaterialCategoryNickname = "resin",
                DensityGramPerCubicCm = 1.1,
                Source = McpMeasurementSource.Volume,
                InitialAmount = 1000, // ml
                InitialLayerTimeS = 30,
                LayerTimeS = 2.5,
                InertGas = "Argon",
                MaterialRefreshRatio = 0.5,
            });

            var m = result.Material;
            Assert.Equal("Volume", m.SourceUnit);
            Assert.Equal(1000d, m.InitialAmountInSourceUnit);
            Assert.Equal(1100d, m.InitialCapacityGrams); // 1000 ml * 1.1 g/cm^3, derived
            Assert.Equal(30d, m.InitialLayerTimeS);
            Assert.Equal(2.5, m.LayerTimeS);
            Assert.Equal("Argon", m.InertGas);
            Assert.Equal(0.5, m.MaterialRefreshRatio);
        }

        [Fact]
        public async Task Create_ColorHexOnly_DerivesColorsArray()
        {
            using var scope = _factory.Services.CreateScope();
            var m = (await Create(scope, Basic() with { ColorHex = "1188FF" })).Material;
            Assert.Equal("1188FF", m.ColorHex);
            Assert.Equal(new[] { "1188FF" }, m.Colors);
        }

        [Fact]
        public async Task Create_ColorsWinOverColorHex_OnDisagreement()
        {
            using var scope = _factory.Services.CreateScope();
            var m = (await Create(scope, Basic() with { ColorHex = "000000", Colors = new[] { "1188FF" } })).Material;
            Assert.Equal("1188FF", m.ColorHex);
        }

        [Fact]
        public async Task Create_EmptyColorsArray_ClearsBothColorFields()
        {
            using var scope = _factory.Services.CreateScope();
            var m = (await Create(scope, Basic() with { ColorHex = "1188FF", Colors = Array.Empty<string>() })).Material;
            Assert.Null(m.ColorHex);
            Assert.Empty(m.Colors);
        }

        [Fact]
        public async Task Create_UnknownCategory_IsInvalid_NotSilentFallback()
        {
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Create(scope, Basic() with { MaterialCategoryNickname = "unobtainium" }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        // There is no tool that lists material categories, so a rejection that does not name the
        // valid options leaves an agent guessing. The set is a small fixed seed (filament, resin,
        // powder, wire) shared by every user, so the error can simply carry it.
        [Fact]
        public async Task Create_UnknownCategory_ErrorNamesTheValidCategories()
        {
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Create(scope, Basic() with { MaterialCategoryNickname = "unobtainium" }));

            Assert.Contains("filament", ex.Message);
            Assert.Contains("resin", ex.Message);
        }

        [Fact]
        public async Task Create_DiameterCategoryWithoutDiameter_IsInvalid()
        {
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Create(scope, Basic() with { DiameterMm = null }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Create_CapacityBeyondLong_IsInvalid_NotOverflowed()
        {
            // A huge density on a Volume source drives the converted milligrams past long.MaxValue.
            // MeasurementUtilities casts UNCHECKED, so without the guard this stores garbage.
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(() => Create(scope, new MaterialAttributesInput
            {
                DisplayName = "Neutronium",
                MaterialType = "Dense",
                MaterialCategoryNickname = "resin",
                DensityGramPerCubicCm = 1e16,
                Source = McpMeasurementSource.Volume,
                InitialAmount = 2_000_000,
            }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Create_LengthSourceWithoutDiameter_IsInvalid_NotACrash()
        {
            // A resin category does not track diameter, so the measurement fill's early return does
            // not fire — it reaches DiameterMm.Value and throws. Reject it up front.
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(() => Create(scope, new MaterialAttributesInput
            {
                DisplayName = "Resin By Length",
                MaterialType = "Standard Resin",
                MaterialCategoryNickname = "resin",
                DensityGramPerCubicCm = 1.1,
                Source = McpMeasurementSource.Length,
                InitialAmount = 1000,
            }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Create_LengthCapacityBeyondLong_IsInvalid()
        {
            // Length source, huge density: the converted milligrams blow past long.MaxValue.
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(() => Create(scope, Basic("Dense Filament") with
            {
                DensityGramPerCubicCm = 1e15,
                Source = McpMeasurementSource.Length,
                InitialAmount = 2_000_000,
            }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Create_SubMilligramCapacity_IsRejected_NotSilentlyZero()
        {
            // 0.0004 g rounds to 0 mg. Stored, that is a material asserting a tracked capacity of
            // nothing — not an error the caller ever sees.
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Create(scope, Basic("Dust") with { InitialAmount = 0.0004 }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Create_LargeWeightAtCap_Succeeds()
        {
            // The Weight source is bounded by MaxAmountMagnitude (2e6 g -> 2e9 mg), far under long.
            using var scope = _factory.Services.CreateScope();
            var m = (await Create(scope, Basic("Huge") with { InitialAmount = 2_000_000 })).Material;
            Assert.Equal(2_000_000d, m.InitialAmountInSourceUnit);
        }

        [Fact]
        public async Task Create_NoKey_CreatesTwoDistinctMaterials()
        {
            using var scope = _factory.Services.CreateScope();
            var first = await Create(scope, Basic("Unkeyed"));
            var second = await Create(scope, Basic("Unkeyed"));
            Assert.NotEqual(first.Material.Id, second.Material.Id);
            Assert.False(second.WasReplayed);
        }

        [Fact]
        public async Task Create_SameKeySamePayload_Replays()
        {
            using var scope = _factory.Services.CreateScope();
            var first = await Create(scope, Basic("Keyed"), "mat-key-1");
            var second = await Create(scope, Basic("Keyed"), "mat-key-1");
            Assert.Equal(first.Material.Id, second.Material.Id);
            Assert.False(first.WasReplayed);
            Assert.True(second.WasReplayed);
        }

        [Fact]
        public async Task Create_SameKeyDifferentPayload_Conflicts()
        {
            using var scope = _factory.Services.CreateScope();
            await Create(scope, Basic("Original"), "mat-key-2");
            var ex = await Assert.ThrowsAsync<McpToolException>(() => Create(scope, Basic("Changed"), "mat-key-2"));
            Assert.Equal("conflict", ex.Code);
        }

        [Fact]
        public async Task Create_SameKeyWhitespaceOnlyDifference_ReplaysConsistently()
        {
            // Canonicalization happens BEFORE both hashing and persistence, so the stored row must
            // match what the fingerprint claims is equivalent — never a trimmed hash over an
            // untrimmed row.
            using var scope = _factory.Services.CreateScope();
            var first = await Create(scope, Basic("  Padded  ") with { Notes = "  a note  " }, "mat-key-3");
            Assert.Equal("Padded", first.Material.DisplayName);
            Assert.Equal("a note", first.Material.Notes);

            var second = await Create(scope, Basic("Padded") with { Notes = "a note" }, "mat-key-3");
            Assert.True(second.WasReplayed);
            Assert.Equal(first.Material.Id, second.Material.Id);
        }

        [Fact]
        public async Task Create_BlankOrOverLongKey_IsInvalid()
        {
            using var scope = _factory.Services.CreateScope();
            Assert.Equal("invalid_arguments",
                (await Assert.ThrowsAsync<McpToolException>(() => Create(scope, Basic(), "   "))).Code);
            Assert.Equal("invalid_arguments",
                (await Assert.ThrowsAsync<McpToolException>(() => Create(scope, Basic(), new string('k', 201)))).Code);
        }

        [Fact]
        public async Task Create_NullFingerprintLegacyRecord_ReplaysWithoutComparison()
        {
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var seeded = await Create(scope, Basic("Legacy Target"));
            context.McpIdempotencyRecords.Add(new McpIdempotencyRecord
            {
                UserId = IntegrationTestSeeder.TestUserId,
                ToolName = "create_material",
                IdempotencyKey = "mat-legacy",
                RequestFingerprint = null, // pre-migration row: no payload to compare against
                CreatedFilamentId = seeded.Material.Id,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();

            // Deliberately DIFFERENT arguments: with no stored fingerprint there is nothing to
            // contradict, so this replays rather than conflicting.
            var replay = await Create(scope, Basic("Totally Different"), "mat-legacy");
            Assert.True(replay.WasReplayed);
            Assert.Equal(seeded.Material.Id, replay.Material.Id);
        }

        [Fact]
        public async Task Create_DanglingRecord_IsNotFound()
        {
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            context.McpIdempotencyRecords.Add(new McpIdempotencyRecord
            {
                UserId = IntegrationTestSeeder.TestUserId,
                ToolName = "create_material",
                IdempotencyKey = "mat-dangling",
                RequestFingerprint = McpRequestFingerprint.ComputeCreateMaterial(Basic("Ghost").Canonicalize()),
                CreatedFilamentId = Guid.NewGuid(), // points at nothing
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<McpToolException>(() => Create(scope, Basic("Ghost"), "mat-dangling"));
            Assert.Equal("not_found", ex.Code);
        }

        [Fact]
        public async Task Create_RecordPointingAtForeignMaterial_IsNotFound()
        {
            // Ownership is re-checked on replay: a record naming another user's material must never
            // hand that material back.
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            context.McpIdempotencyRecords.Add(new McpIdempotencyRecord
            {
                UserId = IntegrationTestSeeder.TestUserId,
                ToolName = "create_material",
                IdempotencyKey = "mat-foreign",
                RequestFingerprint = McpRequestFingerprint.ComputeCreateMaterial(Basic("Foreign").Canonicalize()),
                CreatedFilamentId = McpTestData.ForeignMaterialId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<McpToolException>(() => Create(scope, Basic("Foreign"), "mat-foreign"));
            Assert.Equal("not_found", ex.Code);
        }

        [Fact]
        public async Task Create_BumpsCacheVersion()
        {
            var cache = _factory.Services.GetRequiredService<ICacheVersionService>();
            using var scope = _factory.Services.CreateScope();
            var before = cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId);
            await Create(scope, Basic("Cache Bump"));
            Assert.NotEqual(before, cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId));
        }

        [Fact]
        public async Task Create_Replay_DoesNotBumpCacheVersion()
        {
            var cache = _factory.Services.GetRequiredService<ICacheVersionService>();
            using var scope = _factory.Services.CreateScope();
            await Create(scope, Basic("Replay Cache"), "mat-key-4");
            var afterFirst = cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId);
            var replay = await Create(scope, Basic("Replay Cache"), "mat-key-4");
            Assert.True(replay.WasReplayed);
            Assert.Equal(afterFirst, cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId));
        }
    }
}
