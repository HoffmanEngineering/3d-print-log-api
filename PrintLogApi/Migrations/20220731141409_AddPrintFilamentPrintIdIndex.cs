using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    public partial class AddPrintFilamentPrintIdIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrintFilament_PrintId",
                table: "PrintFilament");

            migrationBuilder.CreateIndex(
                name: "IX_PrintFilament_PrintId",
                table: "PrintFilament",
                column: "PrintId")
                .Annotation("SqlServer:Include", new[] { "FilamentId", "AmountMg", "EstimatedAmountMg" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrintFilament_PrintId",
                table: "PrintFilament");

            migrationBuilder.CreateIndex(
                name: "IX_PrintFilament_PrintId",
                table: "PrintFilament",
                column: "PrintId");
        }
    }
}
