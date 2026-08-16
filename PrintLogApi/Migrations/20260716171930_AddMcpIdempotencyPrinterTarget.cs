using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpIdempotencyPrinterTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive only: a nullable column. The old app version keeps running against this
            // database while the migration applies, and it never writes or reads this column.
            migrationBuilder.AddColumn<long>(
                name: "CreatedPrinterId",
                table: "McpIdempotencyRecords",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedPrinterId",
                table: "McpIdempotencyRecords");
        }
    }
}
