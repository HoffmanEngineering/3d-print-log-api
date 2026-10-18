using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CatalogModelId",
                table: "Printers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "Printers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PrinterModels",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Make = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AliasesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Technology = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BedWidthMm = table.Column<double>(type: "float", nullable: true),
                    BedDepthMm = table.Column<double>(type: "float", nullable: true),
                    BedHeightMm = table.Column<double>(type: "float", nullable: true),
                    NozzleOptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DefaultNozzleMm = table.Column<double>(type: "float", nullable: true),
                    FilamentDiameterMm = table.Column<double>(type: "float", nullable: true),
                    HasHeatedBed = table.Column<bool>(type: "bit", nullable: true),
                    HasHeatedChamber = table.Column<bool>(type: "bit", nullable: true),
                    ExtruderCount = table.Column<int>(type: "int", nullable: true),
                    DefaultSlotCount = table.Column<int>(type: "int", nullable: true),
                    Kinematics = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourcesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Featured = table.Column<int>(type: "int", nullable: true),
                    Hidden = table.Column<bool>(type: "bit", nullable: false),
                    Retired = table.Column<bool>(type: "bit", nullable: false),
                    OwnerCount = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterModels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Printers_CatalogModelId",
                table: "Printers",
                column: "CatalogModelId");

            migrationBuilder.AddForeignKey(
                name: "FK_Printers_PrinterModels_CatalogModelId",
                table: "Printers",
                column: "CatalogModelId",
                principalTable: "PrinterModels",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Printers_PrinterModels_CatalogModelId",
                table: "Printers");

            migrationBuilder.DropTable(
                name: "PrinterModels");

            migrationBuilder.DropIndex(
                name: "IX_Printers_CatalogModelId",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "CatalogModelId",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Printers");
        }
    }
}
