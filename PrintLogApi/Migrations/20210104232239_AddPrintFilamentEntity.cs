using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class AddPrintFilamentEntity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrintFilament",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrintId = table.Column<long>(type: "bigint", nullable: false),
                    FilamentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EstimatedAmountMg = table.Column<int>(type: "int", nullable: true),
                    AmountMg = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintFilament", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrintFilament_Filaments_FilamentId",
                        column: x => x.FilamentId,
                        principalTable: "Filaments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PrintFilament_Prints_PrintId",
                        column: x => x.PrintId,
                        principalTable: "Prints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrintFilament_FilamentId",
                table: "PrintFilament",
                column: "FilamentId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintFilament_PrintId",
                table: "PrintFilament",
                column: "PrintId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrintFilament");
        }
    }
}
