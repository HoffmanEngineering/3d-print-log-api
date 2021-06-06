using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class AddPrinterFilamentEntity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrinterFilament",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrinterId = table.Column<long>(type: "bigint", nullable: false),
                    FilamentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LoadedDateTime = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UnloadedDateTime = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterFilament", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterFilament_Filaments_FilamentId",
                        column: x => x.FilamentId,
                        principalTable: "Filaments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PrinterFilament_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFilament_FilamentId",
                table: "PrinterFilament",
                column: "FilamentId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterFilament_PrinterId",
                table: "PrinterFilament",
                column: "PrinterId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrinterFilament");
        }
    }
}
