using System;
using System.Security.Claims;
using PrintLogApi.Mcp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    public class McpHelpersTests
    {
        private static ClaimsPrincipal PrincipalWithId(string? id) =>
            new(new ClaimsIdentity(id == null
                ? Array.Empty<Claim>()
                : new[] { new Claim(ClaimTypes.NameIdentifier, id) }));

        [Theory]
        [InlineData(null, 25)]
        [InlineData(0, 1)]
        [InlineData(50, 50)]
        [InlineData(1000, 100)]
        public void ClampPageSize_Bounds(int? requested, int expected) =>
            Assert.Equal(expected, McpPaging.ClampPageSize(requested));

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        public void RequirePage_Invalid_Throws(int page) =>
            Assert.Throws<McpToolException>(() => McpPaging.RequirePage(page));

        [Fact]
        public void RequirePage_Valid_Returns() =>
            Assert.Equal(3, McpPaging.RequirePage(3));

        [Fact]
        public void RequireUserId_NullId_Throws() =>
            Assert.Throws<McpToolException>(() => McpUserContext.RequireUserId(PrincipalWithId(null)));

        [Fact]
        public void RequireUserId_ValidId_Returns() =>
            Assert.Equal(7L, McpUserContext.RequireUserId(PrincipalWithId("7")));

        [Fact]
        public void IsCreator_Matches()
        {
            Assert.True(McpUserContext.IsCreator(5, 5));
            Assert.False(McpUserContext.IsCreator(5, 6));
        }

        [Fact]
        public void RequireUtcRange_Over366Days_Throws()
        {
            var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            Assert.Throws<McpToolException>(() => McpValidation.RequireUtcRange(from, from.AddDays(367)));
        }

        [Fact]
        public void RequireUtcRange_Exactly366Days_Accepted()
        {
            var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var (rf, rt) = McpValidation.RequireUtcRange(from, from.AddDays(366));
            Assert.Equal(from, rf);
            Assert.Equal(from.AddDays(366), rt);
        }

        [Fact]
        public void RequireUtcRange_FromAfterTo_Throws()
        {
            var from = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
            Assert.Throws<McpToolException>(() => McpValidation.RequireUtcRange(from, from.AddDays(-1)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void RequirePositiveGrams_Invalid_Throws(double grams) =>
            Assert.Throws<McpToolException>(() => McpValidation.RequirePositiveGrams(grams));

        [Fact]
        public void RequirePositiveGrams_Valid_Returns() =>
            Assert.Equal(12.5, McpValidation.RequirePositiveGrams(12.5));
    }
}
