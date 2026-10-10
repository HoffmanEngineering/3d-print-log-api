using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Slot",
                table: "PrintFilament",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SlotCount",
                table: "Printers",
                type: "int",
                nullable: false,
                // Hand-edited from 0: every existing printer is single-tool. The constraint also
                // stays in place, so the previous app version, still running while this applies,
                // creates printers with one slot rather than none.
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "Slot",
                table: "PrinterFilament",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SlotLabel",
                table: "PrinterFilament",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Slot",
                table: "PrintFilament");

            migrationBuilder.DropColumn(
                name: "SlotCount",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "Slot",
                table: "PrinterFilament");

            migrationBuilder.DropColumn(
                name: "SlotLabel",
                table: "PrinterFilament");
        }
    }
}
