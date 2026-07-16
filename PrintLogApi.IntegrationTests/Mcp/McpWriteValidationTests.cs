using PrintLogApi.Mcp;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>Unit tests for the shared write-tool input validation.</summary>
    public class McpWriteValidationTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void RequirePositiveAmount_Rejects(double value) =>
            Assert.Throws<McpToolException>(() => McpWriteValidation.RequirePositiveAmount(value));

        [Fact]
        public void RequirePositiveAmount_AllowsPositiveFinite() =>
            Assert.Equal(12.5, McpWriteValidation.RequirePositiveAmount(12.5));

        [Theory]
        [InlineData(2_000_000.5)]      // just over the positive cap
        [InlineData(-2_000_000.5)]     // just under the negative cap
        public void RequireFiniteAmount_RejectsOverMagnitudeCap(double value) =>
            Assert.Throws<McpToolException>(() => McpWriteValidation.RequireFiniteAmount(value));

        [Fact]
        public void RequireFiniteAmount_AllowsAtCap() =>
            Assert.Equal(McpWriteValidation.MaxAmountMagnitude,
                McpWriteValidation.RequireFiniteAmount(McpWriteValidation.MaxAmountMagnitude));

        [Fact]
        public void RequirePositiveDuration_RejectsZeroAndNegative()
        {
            Assert.Throws<McpToolException>(() => McpWriteValidation.RequirePositiveDuration(0));
            Assert.Throws<McpToolException>(() => McpWriteValidation.RequirePositiveDuration(-5));
            Assert.Equal(60, McpWriteValidation.RequirePositiveDuration(60));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        public void RequirePositiveDensity_Rejects(double value) =>
            Assert.Throws<McpToolException>(() => McpWriteValidation.RequirePositiveDensity(value));

        [Fact]
        public void RequireMaxLength_RejectsTooLong() =>
            Assert.Throws<McpToolException>(() => McpWriteValidation.RequireMaxLength(new string('x', 101), 100, "title"));

        [Fact]
        public void RequireMaxLength_AllowsNullAndWithinLimit()
        {
            Assert.Null(McpWriteValidation.RequireMaxLength(null, 100, "title"));
            Assert.Equal("ok", McpWriteValidation.RequireMaxLength("ok", 100, "title"));
        }

        [Fact]
        public void RequireDefinedEnum_RejectsUndefined() =>
            Assert.Throws<McpToolException>(() => McpWriteValidation.RequireDefinedEnum((Print.PrintStatus)999, "status"));

        [Fact]
        public void RequireDefinedEnum_AllowsDefined() =>
            Assert.Equal(Print.PrintStatus.Success, McpWriteValidation.RequireDefinedEnum(Print.PrintStatus.Success, "status"));
    }
}
