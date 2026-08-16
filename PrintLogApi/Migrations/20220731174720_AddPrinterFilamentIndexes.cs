using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    public partial class AddPrinterFilamentIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrinterFilament_FilamentId",
                table: "PrinterFilament");

            migrationBuilder.DropIndex(
                name: "IX_PrinterFilament_PrinterId",
                table: "PrinterFilament");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFilament_FilamentId",
                table: "PrinterFilament",
                column: "FilamentId",
                filter: "[UnloadedDateTime] IS NULL")
                .Annotation("SqlServer:Include", new[] { "PrinterId", "LoadedDateTime", "UnloadedDateTime" });

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFilament_PrinterId",
                table: "PrinterFilament",
                column: "PrinterId",
                filter: "[UnloadedDateTime] IS NULL")
                .Annotation("SqlServer:Include", new[] { "FilamentId", "LoadedDateTime", "UnloadedDateTime" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrinterFilament_FilamentId",
                table: "PrinterFilament");

            migrationBuilder.DropIndex(
                name: "IX_PrinterFilament_PrinterId",
                table: "PrinterFilament");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFilament_FilamentId",
                table: "PrinterFilament",
                column: "FilamentId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFilament_PrinterId",
                table: "PrinterFilament",
                column: "PrinterId");
        }
    }
}
