using System;
using System.Collections.Generic;
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
    public class UpdateMaterialServiceTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;
        public UpdateMaterialServiceTests(McpDataWebApplicationFactory factory) => _factory = factory;
        private IFilamentService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IFilamentService>();

        private static ISet<string> Clear(params string[] n) => new HashSet<string>(n);
        private static readonly ISet<string> None = new HashSet<string>();

        private async Task<Guid> Seed(IServiceScope scope, string name, MaterialAttributesInput? extra = null)
        {
            var input = new MaterialAttributesInput
            {
                DisplayName = name,
                MaterialType = "PLA",
                MaterialCategoryNickname = "filament",
                DensityGramPerCubicCm = 1.24,
                DiameterMm = 1.75,
                Source = McpMeasurementSource.Weight,
                InitialAmount = 1000,
                Brand = "Acme",
                Notes = "orig notes",
                ColorHex = "1188FF",
            };
            if (extra != null)
            {
                input = extra with
                {
                    DisplayName = name,
                    MaterialCategoryNickname = extra.MaterialCategoryNickname ?? "filament",
                };
            }
            var created = await Svc(scope).CreateMaterialForMcp(
                IntegrationTestSeeder.TestUserId, input, null, CancellationToken.None);
            return created.Material.Id;
        }

        private Task<MaterialDetail> Update(IServiceScope s, Guid id, MaterialAttributesInput input, ISet<string>? clear = null) =>
            Svc(s).UpdateOwnMaterialForMcp(IntegrationTestSeeder.TestUserId, id, input, clear ?? None, CancellationToken.None);

        [Fact]
        public async Task Update_SetsOnlyProvidedFields()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Patch Me");
            var m = await Update(scope, id, new MaterialAttributesInput { Brand = "NewBrand" });
            Assert.Equal("NewBrand", m.Brand);
            Assert.Equal("orig notes", m.Notes);       // untouched
            Assert.Equal("Patch Me", m.DisplayName);   // untouched
        }

        [Fact]
        public async Task Update_ClearsNullableField()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Clear Me");
            var m = await Update(scope, id, new MaterialAttributesInput(), Clear("notes", "brand"));
            Assert.Null(m.Notes);
            Assert.Null(m.Brand);
        }

        [Fact]
        public async Task Update_SetAndClearSameField_IsInvalid()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Conflict Me");
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Update(scope, id, new MaterialAttributesInput { Notes = "new" }, Clear("notes")));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Update_ClearingColorHexOrColors_ClearsBothJointly()
        {
            // A stale Colors[0] must not resurrect ColorHex — the entity keeps them in sync.
            using var scope = _factory.Services.CreateScope();
            var byHex = await Seed(scope, "Color A");
            var a = await Update(scope, byHex, new MaterialAttributesInput(), Clear("colorHex"));
            Assert.Null(a.ColorHex);
            Assert.Empty(a.Colors);

            var byColors = await Seed(scope, "Color B");
            var b = await Update(scope, byColors, new MaterialAttributesInput(), Clear("colors"));
            Assert.Null(b.ColorHex);
            Assert.Empty(b.Colors);
        }

        [Fact]
        public async Task Update_ColorPatternAndFinish_RoundTripNull()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Pattern Me");
            var m = await Update(scope, id, new MaterialAttributesInput(), Clear("colorPattern", "finishType", "effects"));
            Assert.Null(m.ColorPattern);
            Assert.Null(m.FinishType);
            Assert.Empty(m.Effects);
        }

        [Fact]
        public async Task Update_ForeignMaterial_IsNotFound()
        {
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(() => Update(
                scope, McpTestData.ForeignMaterialId, new MaterialAttributesInput { Brand = "HIJACKED" }));
            Assert.Equal("not_found", ex.Code);
        }

        [Fact]
        public async Task Update_SourceWithoutInitialAmount_IsInvalid()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Half Source");
            Assert.Equal("invalid_arguments", (await Assert.ThrowsAsync<McpToolException>(
                () => Update(scope, id, new MaterialAttributesInput { Source = McpMeasurementSource.Length }))).Code);
            Assert.Equal("invalid_arguments", (await Assert.ThrowsAsync<McpToolException>(
                () => Update(scope, id, new MaterialAttributesInput { InitialAmount = 500 }))).Code);
        }

        [Fact]
        public async Task Update_SourceAndAmountTogether_Rebases()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Rebase Me");
            var m = await Update(scope, id, new MaterialAttributesInput
            {
                Source = McpMeasurementSource.Volume,
                InitialAmount = 100, // ml
            });
            Assert.Equal("Volume", m.SourceUnit);
            Assert.Equal(100d, m.InitialAmountInSourceUnit);
            Assert.Equal(124d, m.InitialCapacityGrams); // 100 ml * 1.24, derived from the source
        }

        [Fact]
        public async Task Update_DensityOnly_RecomputesWeightOnVolumeSource()
        {
            // Documented API parity: the source amount is authoritative and preserved; weight — and
            // therefore remaining-by-weight — FOLLOWS it. Asserting preservation here would be wrong.
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Volume Source", new MaterialAttributesInput
            {
                MaterialType = "Standard Resin",
                MaterialCategoryNickname = "resin",
                DensityGramPerCubicCm = 1.0,
                Source = McpMeasurementSource.Volume,
                InitialAmount = 1000, // ml
            });

            var m = await Update(scope, id, new MaterialAttributesInput { DensityGramPerCubicCm = 2.0 });
            Assert.Equal(1000d, m.InitialAmountInSourceUnit);  // authoritative, preserved
            Assert.Equal(2000d, m.InitialCapacityGrams);       // derived, recomputed
        }

        [Fact]
        public async Task Update_ClearingNonClearableField_IsInvalid()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "No Clear");
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Update(scope, id, new MaterialAttributesInput(), Clear("displayName")));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Update_UnknownCategory_IsInvalid()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Recategorize");
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Update(scope, id, new MaterialAttributesInput { MaterialCategoryNickname = "unobtainium" }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Update_ToDiameterCategoryWithoutDiameter_IsInvalid()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Resin To Filament", new MaterialAttributesInput
            {
                MaterialType = "Standard Resin",
                MaterialCategoryNickname = "resin",
                DensityGramPerCubicCm = 1.1,
                Source = McpMeasurementSource.Volume,
                InitialAmount = 500,
            });
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Update(scope, id, new MaterialAttributesInput { MaterialCategoryNickname = "filament" }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Update_DensityDrivingCapacityBeyondLong_IsInvalid_AndLeavesMaterialUntouched()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Overflow Me", new MaterialAttributesInput
            {
                MaterialType = "Standard Resin",
                MaterialCategoryNickname = "resin",
                DensityGramPerCubicCm = 1.1,
                Source = McpMeasurementSource.Volume,
                InitialAmount = 2_000_000,
            });

            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Update(scope, id, new MaterialAttributesInput { DensityGramPerCubicCm = 1e16, Brand = "SHOULD NOT STICK" }));
            Assert.Equal("invalid_arguments", ex.Code);

            // Re-read in a FRESH scope. Reading through the same scope would query the database and
            // pass even if the rejected mutations were still sitting dirty in the change tracker —
            // the assertion has to see what a later request would see.
            using var verifyScope = _factory.Services.CreateScope();
            var still = await Svc(verifyScope).GetOwnMaterialDetailForMcp(
                IntegrationTestSeeder.TestUserId, id, CancellationToken.None);
            Assert.Equal(1.1, still.DensityGramPerCubicCm);
            Assert.NotEqual("SHOULD NOT STICK", still.Brand);
        }

        [Fact]
        public async Task Update_RejectedEdit_LeavesNoDirtyStateForALaterSave()
        {
            // Proves the rejection path actually discards its half-applied mutations, rather than
            // relying on nothing else calling SaveChanges in this scope.
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var id = await Seed(scope, "Dirty State");

            await Assert.ThrowsAsync<McpToolException>(
                () => Update(scope, id, new MaterialAttributesInput { MaterialCategoryNickname = "unobtainium", Brand = "SHOULD NOT STICK" }));

            // A later save in the same scope must not flush the rejected edit.
            await context.SaveChangesAsync();

            using var verifyScope = _factory.Services.CreateScope();
            var still = await Svc(verifyScope).GetOwnMaterialDetailForMcp(
                IntegrationTestSeeder.TestUserId, id, CancellationToken.None);
            Assert.Equal("Acme", still.Brand);
        }

        [Fact]
        public async Task Update_StartAloneAgainstStoredEnd_IsInvalid()
        {
            // The request carries only start; end lives on the stored row. Validating the fragment
            // alone would store an inverted range.
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Temp Range", new MaterialAttributesInput
            {
                MaterialType = "PLA",
                MaterialCategoryNickname = "filament",
                DensityGramPerCubicCm = 1.24,
                DiameterMm = 1.75,
                Source = McpMeasurementSource.Weight,
                InitialAmount = 1000,
                TempRangeStartC = 190,
                TempRangeEndC = 220,
            });

            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Update(scope, id, new MaterialAttributesInput { TempRangeStartC = 250 }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Update_ToLengthSourceWithoutDiameter_IsInvalid_NotACrash()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Resin Rebase", new MaterialAttributesInput
            {
                MaterialType = "Standard Resin",
                MaterialCategoryNickname = "resin",
                DensityGramPerCubicCm = 1.1,
                Source = McpMeasurementSource.Volume,
                InitialAmount = 500,
            });

            var ex = await Assert.ThrowsAsync<McpToolException>(() => Update(scope, id, new MaterialAttributesInput
            {
                Source = McpMeasurementSource.Length,
                InitialAmount = 1000,
            }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Update_ClearingDiameterOnDiameterCategory_IsInvalid()
        {
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Keep Diameter");
            var ex = await Assert.ThrowsAsync<McpToolException>(
                () => Update(scope, id, new MaterialAttributesInput(), Clear("diameterMm")));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public async Task Update_EveryClearableField_IsAccepted()
        {
            // Guards the allow-list against drift: a name listed as clearable but not handled in the
            // patch would silently do nothing.
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Clear Everything", new MaterialAttributesInput
            {
                MaterialType = "Standard Resin",
                MaterialCategoryNickname = "resin",
                DensityGramPerCubicCm = 1.1,
                Source = McpMeasurementSource.Volume,
                InitialAmount = 500,
                Brand = "Acme",
                ColorName = "Grey",
                ColorHex = "808080",
                StorageLocation = "Shelf",
                Notes = "n",
                PurchaseLocation = "p",
                PurchasePriceValue = "1",
                PurchasePriceCurrency = "USD",
                PurchaseNotes = "pn",
                InertGas = "Argon",
                PurchaseDate = DateTimeOffset.UtcNow,
                SpoolWeightGrams = 10,
                InitialTotalWeightGrams = 20,
                TempRangeStartC = 100,
                TempRangeEndC = 200,
                RecommendedTempC = 150,
                RecommendedBedTempC = 60,
                InitialLayerTimeS = 30,
                LayerTimeS = 2,
                MeltingTemperatureC = 160,
                MaterialRefreshRatio = 0.5,
                ColorPattern = ColorPatternType.Solid,
                FinishType = FilamentFinishType.Standard,
                Effects = new[] { FilamentEffect.Sparkle },
            });

            // diameterMm is omitted: this is a resin (no diameter to clear), and clearing it on a
            // diameter-tracking material is rejected by design — covered separately above.
            var clearable = new HashSet<string>(McpMaterialValidation.ClearableFields);
            clearable.Remove("diameterMm");

            var m = await Update(scope, id, new MaterialAttributesInput(), clearable);

            Assert.Null(m.Brand);
            Assert.Null(m.ColorName);
            Assert.Null(m.ColorHex);
            Assert.Empty(m.Colors);
            Assert.Null(m.StorageLocation);
            Assert.Null(m.Notes);
            Assert.Null(m.PurchaseLocation);
            Assert.Null(m.PurchasePriceValue);
            Assert.Null(m.PurchasePriceCurrency);
            Assert.Null(m.PurchaseNotes);
            Assert.Null(m.InertGas);
            Assert.Null(m.PurchaseDate);
            Assert.Null(m.SpoolWeightGrams);
            Assert.Null(m.InitialTotalWeightGrams);
            Assert.Null(m.TempRangeStartC);
            Assert.Null(m.TempRangeEndC);
            Assert.Null(m.RecommendedTempC);
            Assert.Null(m.RecommendedBedTempC);
            Assert.Null(m.InitialLayerTimeS);
            Assert.Null(m.LayerTimeS);
            Assert.Null(m.MeltingTemperatureC);
            Assert.Null(m.MaterialRefreshRatio);
            Assert.Null(m.ColorPattern);
            Assert.Null(m.FinishType);
            Assert.Empty(m.Effects);
            // Capacity survives: it is not clearable.
            Assert.Equal(500d, m.InitialAmountInSourceUnit);
        }

        [Fact]
        public async Task Update_BumpsCacheVersion_OnSuccess_NotOnRejection()
        {
            var cache = _factory.Services.GetRequiredService<ICacheVersionService>();
            using var scope = _factory.Services.CreateScope();
            var id = await Seed(scope, "Cache Me");

            var beforeRejected = cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId);
            await Assert.ThrowsAsync<McpToolException>(
                () => Update(scope, id, new MaterialAttributesInput { MaterialCategoryNickname = "unobtainium" }));
            Assert.Equal(beforeRejected, cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId));

            var before = cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId);
            await Update(scope, id, new MaterialAttributesInput { Brand = "Bumped" });
            Assert.NotEqual(before, cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId));
        }
    }
}
