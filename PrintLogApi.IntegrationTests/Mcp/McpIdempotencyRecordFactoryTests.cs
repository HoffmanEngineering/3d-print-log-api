using System;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// The exactly-one-target rule is a code invariant, not a schema one: three nullable columns
    /// share one table. These tests pin the single construction path that makes it true.
    /// </summary>
    public class McpIdempotencyRecordFactoryTests
    {
        [Fact]
        public void ForPrinter_SetsOnlyThePrinterTarget()
        {
            var r = McpIdempotencyRecordFactory.ForPrinter(7, "k", "fp", printerId: 42);

            Assert.Equal(42, r.CreatedPrinterId);
            Assert.Null(r.CreatedPrintId);
            Assert.Null(r.CreatedFilamentId);
            Assert.Equal("create_printer", r.ToolName);
            Assert.Equal(7, r.UserId);
            Assert.Equal("k", r.IdempotencyKey);
            Assert.Equal("fp", r.RequestFingerprint);
            Assert.NotEqual(default, r.CreatedAt);
        }

        [Fact]
        public void ForPrint_SetsOnlyThePrintTarget()
        {
            var r = McpIdempotencyRecordFactory.ForPrint(7, "k", "fp", printId: 42);

            Assert.Equal(42, r.CreatedPrintId);
            Assert.Null(r.CreatedPrinterId);
            Assert.Null(r.CreatedFilamentId);
            Assert.Equal("create_print", r.ToolName);
        }

        [Fact]
        public void ForMaterial_SetsOnlyTheFilamentTarget()
        {
            var id = Guid.NewGuid();
            var r = McpIdempotencyRecordFactory.ForMaterial(7, "k", "fp", id);

            Assert.Equal(id, r.CreatedFilamentId);
            Assert.Null(r.CreatedPrintId);
            Assert.Null(r.CreatedPrinterId);
            Assert.Equal("create_material", r.ToolName);
        }

        // A pairwise XOR of three operands is true for ONE or THREE non-null targets, so the guard
        // counts instead. These two cases are what a count catches and an XOR would not.
        [Fact]
        public void RequireExactlyOneTarget_RejectsZeroTargets()
        {
            var r = new McpIdempotencyRecord { UserId = 1, ToolName = "create_printer", IdempotencyKey = "k" };
            Assert.Throws<InvalidOperationException>(() => McpIdempotencyRecordFactory.RequireExactlyOneTarget(r));
        }

        [Fact]
        public void RequireExactlyOneTarget_RejectsThreeTargets()
        {
            var r = new McpIdempotencyRecord
            {
                UserId = 1,
                ToolName = "create_printer",
                IdempotencyKey = "k",
                CreatedPrintId = 1,
                CreatedFilamentId = Guid.NewGuid(),
                CreatedPrinterId = 1,
            };
            Assert.Throws<InvalidOperationException>(() => McpIdempotencyRecordFactory.RequireExactlyOneTarget(r));
        }

        [Fact]
        public void RequireExactlyOneTarget_RejectsTwoTargets()
        {
            var r = new McpIdempotencyRecord
            {
                UserId = 1,
                ToolName = "create_printer",
                IdempotencyKey = "k",
                CreatedPrintId = 1,
                CreatedPrinterId = 1,
            };
            Assert.Throws<InvalidOperationException>(() => McpIdempotencyRecordFactory.RequireExactlyOneTarget(r));
        }
    }
}
