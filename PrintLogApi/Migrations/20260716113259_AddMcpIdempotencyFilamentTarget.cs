using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpIdempotencyFilamentTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backwards compatible: widening CreatedPrintId to nullable cannot break the old binary,
            // which only ever writes non-null; CreatedFilamentId is purely additive.
            migrationBuilder.AlterColumn<long>(
                name: "CreatedPrintId",
                table: "McpIdempotencyRecords",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedFilamentId",
                table: "McpIdempotencyRecords",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedFilamentId",
                table: "McpIdempotencyRecords");

            migrationBuilder.AlterColumn<long>(
                name: "CreatedPrintId",
                table: "McpIdempotencyRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);
        }
    }
}
