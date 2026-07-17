using System;
using System.Collections.Generic;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>Unit tests for the shared write-tool input validation.</summary>
    public class McpWriteValidationTests
    {
        private static readonly HashSet<string> ClearAllowed = new() { "fileName", "url", "notes", "startedAt" };

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
            Assert.Throws<McpToolException>(() => McpWriteValidation.RequirePositiveDuration(0, "durationSeconds"));
            Assert.Throws<McpToolException>(() => McpWriteValidation.RequirePositiveDuration(-5, "durationSeconds"));
            Assert.Equal(60, McpWriteValidation.RequirePositiveDuration(60, "durationSeconds"));
        }

        [Fact]
        public void RequirePositiveDuration_NamesField()
        {
            var ex = Assert.Throws<McpToolException>(
                () => McpWriteValidation.RequirePositiveDuration(0, "estimatedDurationSeconds"));
            Assert.Contains("estimatedDurationSeconds", ex.Message);
        }

        [Fact]
        public void ClearFields_RejectsUnknown() =>
            Assert.Equal("invalid_arguments",
                Assert.Throws<McpToolException>(
                    () => McpWriteValidation.RequireAllowedClearFields(new[] { "bogus" }, ClearAllowed)).Code);

        [Fact]
        public void ClearFields_NormalizesDedupes()
        {
            var r = McpWriteValidation.RequireAllowedClearFields(new[] { " fileName ", "url", "url" }, ClearAllowed);
            Assert.Equal(2, r.Count);
            Assert.Contains("fileName", r);
        }

        [Fact]
        public void ClearFields_NullReturnsEmpty() =>
            Assert.Empty(McpWriteValidation.RequireAllowedClearFields(null, ClearAllowed));

        [Fact]
        public void UsageRow_NeitherPair_Invalid() =>
            Assert.Throws<McpToolException>(() => PrintLogWriteTools.ValidateUsageRow(
                new MaterialUsageInput(Guid.NewGuid(), null, null, null, null, null)));

        [Fact]
        public void UsageRow_HalfPair_Invalid() =>
            Assert.Throws<McpToolException>(() => PrintLogWriteTools.ValidateUsageRow(
                new MaterialUsageInput(Guid.NewGuid(), McpMeasurementSource.Weight, null, null, null, null)));

        [Fact]
        public void UsageRow_EstimatedOnly_Ok() =>
            PrintLogWriteTools.ValidateUsageRow(
                new MaterialUsageInput(Guid.NewGuid(), null, null, McpMeasurementSource.Weight, 5.0, null));

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
