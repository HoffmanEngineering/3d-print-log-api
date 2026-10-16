using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSpoolTagReading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DecodedJson",
                table: "FilamentTags",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DecoderVersion",
                table: "FilamentTags",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastReadAt",
                table: "FilamentTags",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CatalogEntryId",
                table: "Filaments",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FilamentCatalogEntries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Manufacturer = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Material = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Density = table.Column<double>(type: "float", nullable: false),
                    WeightG = table.Column<int>(type: "int", nullable: false),
                    SpoolWeightG = table.Column<int>(type: "int", nullable: true),
                    SpoolType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    DiameterMm = table.Column<double>(type: "float", nullable: false),
                    ColorHex = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    ColorHexesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExtruderTemp = table.Column<int>(type: "int", nullable: true),
                    ExtruderTempMin = table.Column<int>(type: "int", nullable: true),
                    ExtruderTempMax = table.Column<int>(type: "int", nullable: true),
                    BedTemp = table.Column<int>(type: "int", nullable: true),
                    BedTempMin = table.Column<int>(type: "int", nullable: true),
                    BedTempMax = table.Column<int>(type: "int", nullable: true),
                    Finish = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Pattern = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Translucent = table.Column<bool>(type: "bit", nullable: false),
                    Glow = table.Column<bool>(type: "bit", nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RemovedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilamentCatalogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TagReadFailures",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Uid = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Techs = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MemoryKind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Data = table.Column<byte[]>(type: "varbinary(2048)", maxLength: 2048, nullable: false),
                    DetectedFormat = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagReadFailures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FilamentCatalogSkus",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Format = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CatalogEntryId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilamentCatalogSkus", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FilamentCatalogSkus_FilamentCatalogEntries_CatalogEntryId",
                        column: x => x.CatalogEntryId,
                        principalTable: "FilamentCatalogEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FilamentCatalogSkus_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Filaments_CatalogEntryId",
                table: "Filaments",
                column: "CatalogEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentCatalogEntries_Manufacturer_Material",
                table: "FilamentCatalogEntries",
                columns: new[] { "Manufacturer", "Material" });

            migrationBuilder.CreateIndex(
                name: "IX_FilamentCatalogSkus_CatalogEntryId",
                table: "FilamentCatalogSkus",
                column: "CatalogEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_FilamentCatalogSkus_Format_Sku",
                table: "FilamentCatalogSkus",
                columns: new[] { "Format", "Sku" });

            migrationBuilder.CreateIndex(
                name: "IX_FilamentCatalogSkus_Format_Sku_UserId",
                table: "FilamentCatalogSkus",
                columns: new[] { "Format", "Sku", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilamentCatalogSkus_UserId",
                table: "FilamentCatalogSkus",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TagReadFailures_CreatedAt",
                table: "TagReadFailures",
                column: "CreatedAt");

            migrationBuilder.AddForeignKey(
                name: "FK_Filaments_FilamentCatalogEntries_CatalogEntryId",
                table: "Filaments",
                column: "CatalogEntryId",
                principalTable: "FilamentCatalogEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Filaments_FilamentCatalogEntries_CatalogEntryId",
                table: "Filaments");

            migrationBuilder.DropTable(
                name: "FilamentCatalogSkus");

            migrationBuilder.DropTable(
                name: "TagReadFailures");

            migrationBuilder.DropTable(
                name: "FilamentCatalogEntries");

            migrationBuilder.DropIndex(
                name: "IX_Filaments_CatalogEntryId",
                table: "Filaments");

            migrationBuilder.DropColumn(
                name: "DecodedJson",
                table: "FilamentTags");

            migrationBuilder.DropColumn(
                name: "DecoderVersion",
                table: "FilamentTags");

            migrationBuilder.DropColumn(
                name: "LastReadAt",
                table: "FilamentTags");

            migrationBuilder.DropColumn(
                name: "CatalogEntryId",
                table: "Filaments");
        }
    }
}
