using System;
using System.Collections.Generic;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    public class McpRequestFingerprintTests
    {
        private static string Fp(string title, string notes, IReadOnlyList<MaterialUsageInput> materials) =>
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
    }
}
