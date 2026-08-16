using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Mcp;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    public class GetMaterialToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;
        public GetMaterialToolTests(McpDataWebApplicationFactory factory) => _factory = factory;
        private IFilamentService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IFilamentService>();

        [Fact]
        public async Task Get_ReturnsOwnedMaterial_WithSourceAndCapacity()
        {
            using var scope = _factory.Services.CreateScope();
            var detail = await Svc(scope).GetOwnMaterialDetailForMcp(
                IntegrationTestSeeder.TestUserId, McpTestData.ResinMaterialId, CancellationToken.None);

            Assert.Equal("Elegoo Grey Standard Resin", detail.DisplayName);
            Assert.Equal("resin", detail.CategoryNickname);
            Assert.Equal("Weight", detail.SourceUnit);
            Assert.Equal(1000d, detail.InitialAmountInSourceUnit); // 1,000,000 mg -> 1000 g
            Assert.Equal(1000d, detail.InitialCapacityGrams);
            Assert.True(detail.HasNominalCapacity);
            Assert.Null(detail.DiameterMm);
            Assert.Equal(1.1, detail.DensityGramPerCubicCm);
        }

        [Fact]
        public async Task Get_NoNominalCapacity_IsDistinguishedFromEmpty()
        {
            // InactiveFilamentId has InitialNominalWeightMg = null. RemainingGrams is 0 for BOTH
            // "empty" and "never tracked", so the flag is the only thing separating them.
            using var scope = _factory.Services.CreateScope();
            var detail = await Svc(scope).GetOwnMaterialDetailForMcp(
                IntegrationTestSeeder.TestUserId, McpTestData.InactiveFilamentId, CancellationToken.None);

            Assert.False(detail.HasNominalCapacity);
            Assert.Null(detail.InitialCapacityGrams);
            Assert.Equal(0d, detail.RemainingGrams);
            Assert.False(detail.IsActive);
        }

        [Fact]
        public async Task Get_ForeignMaterial_IsNotFound()
        {
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(() => Svc(scope).GetOwnMaterialDetailForMcp(
                IntegrationTestSeeder.TestUserId, McpTestData.ForeignMaterialId, CancellationToken.None));
            Assert.Equal("not_found", ex.Code);
        }

        [Fact]
        public async Task Get_UnknownId_IsNotFound()
        {
            using var scope = _factory.Services.CreateScope();
            var ex = await Assert.ThrowsAsync<McpToolException>(() => Svc(scope).GetOwnMaterialDetailForMcp(
                IntegrationTestSeeder.TestUserId, Guid.NewGuid(), CancellationToken.None));
            Assert.Equal("not_found", ex.Code);
        }
    }
}
