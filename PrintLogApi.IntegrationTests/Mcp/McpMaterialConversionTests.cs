using PrintLogApi.Mcp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// The guard exists because MeasurementUtilities casts double -> long UNCHECKED: an out-of-range
    /// value would be stored as garbage rather than throwing. These tests pin the boundary.
    /// </summary>
    public class McpMaterialConversionTests
    {
        [Fact]
        public void GramsToMg_Converts()
        {
            Assert.Equal(1_000_000L, McpMaterialConversion.GramsToMg(1000d, "initialAmount"));
        }

        [Fact]
        public void RequireMgInRange_RejectsBeyondLong()
        {
            var ex = Assert.Throws<McpToolException>(
                () => McpMaterialConversion.RequireMgInRange(1e19, "initialAmount"));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public void RequireMgInRange_RejectsNonFinite()
        {
            Assert.Throws<McpToolException>(
                () => McpMaterialConversion.RequireMgInRange(double.NaN, "initialAmount"));
            Assert.Throws<McpToolException>(
                () => McpMaterialConversion.RequireMgInRange(double.PositiveInfinity, "initialAmount"));
        }

        [Fact]
        public void RequireMgInRange_RejectsNegative()
        {
            Assert.Throws<McpToolException>(
                () => McpMaterialConversion.RequireMgInRange(-1d, "spoolWeightGrams"));
        }

        [Fact]
        public void RequireMgInRange_AcceptsZeroAndBoundary()
        {
            Assert.Equal(0L, McpMaterialConversion.RequireMgInRange(0d, "spoolWeightGrams"));
            // Largest double strictly below 2^63, so the long cast is well defined.
            Assert.Equal(9_223_372_036_854_774_784L,
                McpMaterialConversion.RequireMgInRange(9_223_372_036_854_774_784d, "initialAmount"));
        }

        [Fact]
        public void RequireMgInRange_SubMinimum_IsRejected_NotRoundedToZero()
        {
            // A capacity of 0.4 mg must NOT round to 0: a zero InitialNominalWeightMg is stored as a
            // real capacity of nothing, so the material reports "0 g, capacity tracked" forever. This
            // is the same defect class as the sub-milligram print usage bug — rounding before
            // validating hides it.
            var ex = Assert.Throws<McpToolException>(
                () => McpMaterialConversion.RequireMgInRange(0.4, "initialAmount", minMg: 1));
            Assert.Equal("invalid_arguments", ex.Code);
        }

        [Fact]
        public void RequireMgInRange_SmallestRecordableCapacity_IsAccepted()
        {
            Assert.Equal(1L, McpMaterialConversion.RequireMgInRange(1d, "initialAmount", minMg: 1));
        }

        [Fact]
        public void RequireMgInRange_ZeroStaysLegalWhereZeroIsMeaningful()
        {
            // A spool weight of 0 is a real answer ("no spool"), unlike a capacity of 0.
            Assert.Equal(0L, McpMaterialConversion.GramsToMg(0d, "spoolWeightGrams"));
        }
    }
}
