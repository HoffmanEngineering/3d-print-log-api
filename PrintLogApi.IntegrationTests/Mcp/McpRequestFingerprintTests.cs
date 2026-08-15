using System;
using System.Collections.Generic;
using PrintLogApi.Enums;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    public class McpRequestFingerprintTests
    {
        private static string Fp(string title, string? notes, IReadOnlyList<MaterialUsageInput> materials) =>
            McpRequestFingerprint.ComputeCreatePrint(title, 7, Print.PrintStatus.Success, null, 3600, null,
                notes, null, null, null, null, null, null, materials);

        private static MaterialUsageInput W(Guid id, double g) => new(id, McpMeasurementSource.Weight, g, null, null, null);

        [Fact]
        public void Identical_SameFingerprint()
        {
            var a = Fp("Benchy", "n", new List<MaterialUsageInput> { W(Guid.Empty, 18) });
            var b = Fp("Benchy", "n", new List<MaterialUsageInput> { W(Guid.Empty, 18) });
            Assert.Equal(a, b);
            Assert.Equal(64, a.Length);
        }

        [Fact]
        public void RowOrder_DoesNotMatter()
        {
            var id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
            Assert.Equal(
                Fp("t", "n", new List<MaterialUsageInput> { W(id1, 10), W(id2, 5) }),
                Fp("t", "n", new List<MaterialUsageInput> { W(id2, 5), W(id1, 10) }));
        }

        [Fact]
        public void DifferentAmount_DifferentFingerprint() =>
            Assert.NotEqual(Fp("t", "n", new List<MaterialUsageInput> { W(Guid.Empty, 18) }),
                            Fp("t", "n", new List<MaterialUsageInput> { W(Guid.Empty, 19) }));

        [Fact]
        public void NullVsEmptyNotes_Differ() =>
            Assert.NotEqual(Fp("t", null, new List<MaterialUsageInput>()),
                            Fp("t", "", new List<MaterialUsageInput>()));

        [Fact]
        public void SeparatorInjection_DoesNotCollide()
        {
            // A title that tries to look like "title=x, notes=y" must not collide with distinct fields.
            var a = Fp("ax", "by", new List<MaterialUsageInput>());
            var b = Fp("a", "xby", new List<MaterialUsageInput>());
            Assert.NotEqual(a, b);
        }

        private static string FpMat(MaterialAttributesInput input) =>
            McpRequestFingerprint.ComputeCreateMaterial(input);

        [Fact]
        public void Material_SameArguments_SameFingerprint()
        {
            var a = new MaterialAttributesInput { DisplayName = "Spool", DensityGramPerCubicCm = 1.24 };
            var b = new MaterialAttributesInput { DisplayName = "Spool", DensityGramPerCubicCm = 1.24 };
            Assert.Equal(FpMat(a), FpMat(b));
        }

        [Fact]
        public void Material_DifferentArguments_DifferentFingerprint() =>
            Assert.NotEqual(
                FpMat(new MaterialAttributesInput { DisplayName = "Spool" }),
                FpMat(new MaterialAttributesInput { DisplayName = "Other" }));

        [Fact]
        public void Material_HashesValuesExactlyAsGiven_DoesNotTrim()
        {
            // The fingerprint must NOT normalize: callers canonicalize first. If it trimmed here, it
            // would report two calls as identical while the database stored different rows.
            Assert.NotEqual(
                FpMat(new MaterialAttributesInput { DisplayName = "Spool" }),
                FpMat(new MaterialAttributesInput { DisplayName = "  Spool  " }));
        }

        [Fact]
        public void Material_NullAndEmptyString_AreDistinguished() =>
            Assert.NotEqual(
                FpMat(new MaterialAttributesInput { Brand = null }),
                FpMat(new MaterialAttributesInput { Brand = "" }));

        [Fact]
        public void Material_ColorOrder_IsSignificant() =>
            // Colors[0] becomes ColorHex, so a reordered array is a genuinely different request.
            Assert.NotEqual(
                FpMat(new MaterialAttributesInput { Colors = new[] { "AABBCC", "112233" } }),
                FpMat(new MaterialAttributesInput { Colors = new[] { "112233", "AABBCC" } }));

        [Fact]
        public void Material_EffectOrderAndDuplicates_AreNotSignificant() =>
            // Effects are a set: order carries no meaning, so a reorder must still replay.
            Assert.Equal(
                FpMat(new MaterialAttributesInput { Effects = new[] { FilamentEffect.Sparkle, FilamentEffect.WoodFill } }),
                FpMat(new MaterialAttributesInput { Effects = new[] { FilamentEffect.WoodFill, FilamentEffect.Sparkle, FilamentEffect.Sparkle } }));

        [Fact]
        public void Material_FieldBoundary_CannotBeForged() =>
            // Length-prefixed writes: no concatenation of one field can impersonate the next.
            Assert.NotEqual(
                FpMat(new MaterialAttributesInput { DisplayName = "ab", Brand = "c" }),
                FpMat(new MaterialAttributesInput { DisplayName = "a", Brand = "bc" }));

        private static PrinterAttributesInput BasicPrinter() => new()
        {
            Make = "Bambu",
            Model = "X1C",
            Name = "Workshop X1C",
        };

        private static string FpPrn(PrinterAttributesInput input) =>
            McpRequestFingerprint.ComputeCreatePrinter(input);

        [Fact]
        public void CreatePrinter_SameArguments_ProduceTheSameFingerprint()
        {
            Assert.Equal(FpPrn(BasicPrinter()), FpPrn(BasicPrinter()));
        }

        [Fact]
        public void CreatePrinter_AnyChangedField_ChangesTheFingerprint()
        {
            var baseline = FpPrn(BasicPrinter());
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { Make = "Prusa" }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { Model = "MK4" }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { Name = "Other" }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { Description = "d" }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { CategoryNickname = "SLA" }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { NozzleDiameterMm = 0.4 }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { FilamentDiameterMm = 1.75 }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { BeamDiameterMm = 0.05 }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { BedWidthMm = 256 }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { BedDepthMm = 256 }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { BedHeightMm = 256 }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { ScreenResolutionXPixels = 3840 }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { ScreenResolutionYPixels = 2160 }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { HasHeatedBed = true }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { HasHeatedChamber = true }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { WattageW = 350 }));
            Assert.NotEqual(baseline, FpPrn(BasicPrinter() with { IsActive = true }));
        }

        // An omitted value and an explicitly-passed default are different requests: the fingerprint
        // hashes what the CALLER sent, before any server defaulting.
        [Fact]
        public void CreatePrinter_OmittedAndExplicitDefault_AreDifferentRequests()
        {
            Assert.NotEqual(FpPrn(BasicPrinter()), FpPrn(BasicPrinter() with { IsActive = true }));
            Assert.NotEqual(FpPrn(BasicPrinter()), FpPrn(BasicPrinter() with { CategoryNickname = "FFF" }));
        }

        // Null and empty must not collide: the BinaryWriter writes a has-value flag before the
        // string, so "" is a value and null is its absence.
        [Fact]
        public void CreatePrinter_NullAndEmptyString_AreDifferentRequests()
        {
            Assert.NotEqual(
                FpPrn(BasicPrinter() with { Description = null }),
                FpPrn(BasicPrinter() with { Description = "" }));
        }

        // Length-prefixed writes mean a field's own content cannot forge a boundary between fields.
        [Fact]
        public void CreatePrinter_FieldContent_CannotForgeAFieldBoundary()
        {
            Assert.NotEqual(
                FpPrn(BasicPrinter() with { Make = "AB", Model = "C" }),
                FpPrn(BasicPrinter() with { Make = "A", Model = "BC" }));
        }
    }
}
