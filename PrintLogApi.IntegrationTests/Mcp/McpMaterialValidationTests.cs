using System.Collections.Generic;
using PrintLogApi.Enums;
using PrintLogApi.Mcp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    public class McpMaterialValidationTests
    {
        private static McpToolException Invalid(MaterialAttributesInput input) =>
            Assert.Throws<McpToolException>(() => McpMaterialValidation.ValidateAttributes(input));

        [Fact]
        public void TempRangeStartAfterEnd_IsRejected()
        {
            var ex = Invalid(new MaterialAttributesInput { TempRangeStartC = 250, TempRangeEndC = 200 });
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public void TempRangeStartEqualToEnd_IsAllowed()
        {
            McpMaterialValidation.ValidateAttributes(
                new MaterialAttributesInput { TempRangeStartC = 210, TempRangeEndC = 210 });
        }

        [Fact]
        public void NegativeTemperatures_AreAllowed()
        {
            // A cryogenic or sub-zero chamber figure is not our business to reject.
            McpMaterialValidation.ValidateAttributes(
                new MaterialAttributesInput { RecommendedBedTempC = -10 });
        }

        [Fact]
        public void RefreshRatioOutsideUnitInterval_IsRejected()
        {
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { MaterialRefreshRatio = 1.5 }).Code);
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { MaterialRefreshRatio = -0.1 }).Code);
        }

        [Fact]
        public void NonFiniteNumber_IsRejected()
        {
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { RecommendedTempC = double.NaN }).Code);
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { LayerTimeS = double.PositiveInfinity }).Code);
        }

        [Fact]
        public void NegativeCureTimeOrSpoolWeight_IsRejected()
        {
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { LayerTimeS = -1 }).Code);
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { SpoolWeightGrams = -1 }).Code);
        }

        [Fact]
        public void DiameterNonPositive_IsRejected_EvenForNonDiameterCategory()
        {
            // Validated whenever supplied: a category that ignores diameter must still not accept -1.
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { DiameterMm = -1 }).Code);
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { DiameterMm = double.NaN }).Code);
        }

        [Fact]
        public void BadColorHex_IsRejected()
        {
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { ColorHex = "#1188FF" }).Code);
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { ColorHex = "GGGGGG" }).Code);
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { Colors = new[] { "1188FF", "xyz" } }).Code);
        }

        [Fact]
        public void GoodColorHex_IsAccepted()
        {
            McpMaterialValidation.ValidateAttributes(
                new MaterialAttributesInput { ColorHex = "1188ff", Colors = new[] { "AABBCC" } });
        }

        [Fact]
        public void TooManyColors_IsRejected()
        {
            var many = new string[33];
            for (var i = 0; i < many.Length; i++)
            {
                many[i] = "112233";
            }
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { Colors = many }).Code);
        }

        [Fact]
        public void UndefinedEnum_IsRejected()
        {
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { ColorPattern = (ColorPatternType)99 }).Code);
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { Effects = new[] { (FilamentEffect)99 } }).Code);
        }

        [Fact]
        public void OverLengthString_IsRejected()
        {
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { Brand = new string('x', 256) }).Code);
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { Notes = new string('x', 1001) }).Code);
            Assert.Equal("invalid_arguments", Invalid(new MaterialAttributesInput { MaterialCategoryNickname = new string('x', 51) }).Code);
        }

        [Fact]
        public void RequireClearableFields_RejectsNonClearableName()
        {
            // The SERVICE is the enforcement boundary, not the tool wrapper: a service-level caller
            // must not be able to clear an identity field just by bypassing the tool.
            var ex = Assert.Throws<McpToolException>(() => McpMaterialValidation.RequireClearableFields(
                new HashSet<string> { "displayName" }));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public void RequireClearableFields_AcceptsClearableNames()
        {
            McpMaterialValidation.RequireClearableFields(new HashSet<string> { "notes", "brand", "colors" });
        }

        [Fact]
        public void Canonicalize_TrimsEveryString()
        {
            var canonical = new MaterialAttributesInput
            {
                DisplayName = "  Spool  ",
                Brand = "  Acme ",
                Notes = " note ",
                Colors = new[] { " AABBCC " },
            }.Canonicalize();

            Assert.Equal("Spool", canonical.DisplayName);
            Assert.Equal("Acme", canonical.Brand);
            Assert.Equal("note", canonical.Notes);
            Assert.Equal("AABBCC", canonical.Colors![0]);
        }
    }
}
