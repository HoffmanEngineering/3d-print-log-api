using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPrinterAndMaterialCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MaterialCategoryNickname",
                table: "MaterialTypes",
                type: "nvarchar(50)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MaterialCategories",
                columns: table => new
                {
                    Nickname = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialCategories", x => x.Nickname);
                });

            migrationBuilder.CreateTable(
                name: "PrinterCategories",
                columns: table => new
                {
                    Nickname = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    MaterialCategoryNickname = table.Column<string>(type: "nvarchar(50)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterCategories", x => x.Nickname);
                    table.ForeignKey(
                        name: "FK_PrinterCategories_MaterialCategories_MaterialCategoryNickname",
                        column: x => x.MaterialCategoryNickname,
                        principalTable: "MaterialCategories",
                        principalColumn: "Nickname");
                });

            migrationBuilder.InsertData(
                table: "MaterialCategories",
                columns: new[] { "Nickname", "Description", "Name" },
                values: new object[,]
                {
                    { "filament", "A single continuous filament of material", "Filament" },
                    { "powder", "A powder which is fused by heat or a binder", "Powder" },
                    { "resin", "A photo-sensitive resin", "Resin" },
                    { "wire", "A continous wire", "Wire" }
                });

            migrationBuilder.UpdateData(
                table: "MaterialTypes",
                keyColumn: "Id",
                keyValue: new Guid("0c236829-8487-4bb4-a092-68a9731a64e4"),
                column: "MaterialCategoryNickname",
                value: "filament");

            migrationBuilder.UpdateData(
                table: "MaterialTypes",
                keyColumn: "Id",
                keyValue: new Guid("23e38c0d-43f3-4bcd-b3c6-830d193a3e10"),
                column: "MaterialCategoryNickname",
                value: "filament");

            migrationBuilder.UpdateData(
                table: "MaterialTypes",
                keyColumn: "Id",
                keyValue: new Guid("35151bfe-6890-41ab-8fc9-443c5a690626"),
                column: "MaterialCategoryNickname",
                value: "filament");

            migrationBuilder.UpdateData(
                table: "MaterialTypes",
                keyColumn: "Id",
                keyValue: new Guid("3dbc49c5-a493-4e21-a4d5-d94b8c0d53da"),
                column: "MaterialCategoryNickname",
                value: "filament");

            migrationBuilder.UpdateData(
                table: "MaterialTypes",
                keyColumn: "Id",
                keyValue: new Guid("7d83cbc1-00d0-4e42-a7ce-8a1b831b175b"),
                column: "MaterialCategoryNickname",
                value: "filament");

            migrationBuilder.UpdateData(
                table: "MaterialTypes",
                keyColumn: "Id",
                keyValue: new Guid("b0cda842-5a48-4a30-a060-226680e13c06"),
                column: "MaterialCategoryNickname",
                value: "filament");

            migrationBuilder.UpdateData(
                table: "MaterialTypes",
                keyColumn: "Id",
                keyValue: new Guid("c78c56d0-b34d-49b1-849e-a54066a2f5e3"),
                column: "MaterialCategoryNickname",
                value: "filament");

            migrationBuilder.UpdateData(
                table: "MaterialTypes",
                keyColumn: "Id",
                keyValue: new Guid("c8cae1e0-5f13-41d6-9f72-cb83740aa2fe"),
                column: "MaterialCategoryNickname",
                value: "filament");

            migrationBuilder.UpdateData(
                table: "MaterialTypes",
                keyColumn: "Id",
                keyValue: new Guid("d777bde9-fba6-4f5a-b7a4-e8a4a9695715"),
                column: "MaterialCategoryNickname",
                value: "filament");

            migrationBuilder.UpdateData(
                table: "MaterialTypes",
                keyColumn: "Id",
                keyValue: new Guid("f8a6b364-94a6-4a32-a253-e67b41df1969"),
                column: "MaterialCategoryNickname",
                value: "filament");

            migrationBuilder.InsertData(
                table: "PrinterCategories",
                columns: new[] { "Nickname", "Description", "MaterialCategoryNickname", "Name" },
                values: new object[,]
                {
                    { "DLP", "", "resin", "Digital Light Processing" },
                    { "EBM", "", "powder", "Electron Beam Melting" },
                    { "FDM", "Material extruded through a nozzle.", "filament", "Fused Deposition Modeling" },
                    { "FFF", "Material extruded through a nozzle.", "filament", "Fused Filament Fabrication" },
                    { "LCD", "", "resin", "Liquid Crystal Display" },
                    { "LPDF", "", "powder", "Laser Powder Bed Fusion" },
                    { "MSLA", "", "resin", "Micro-stereolithography" },
                    { "SLA", "", "resin", "Stereolithography" },
                    { "SLS", "", "powder", "Selective Laser Sintering" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialTypes_MaterialCategoryNickname",
                table: "MaterialTypes",
                column: "MaterialCategoryNickname");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterCategories_MaterialCategoryNickname",
                table: "PrinterCategories",
                column: "MaterialCategoryNickname");

            migrationBuilder.AddForeignKey(
                name: "FK_MaterialTypes_MaterialCategories_MaterialCategoryNickname",
                table: "MaterialTypes",
                column: "MaterialCategoryNickname",
                principalTable: "MaterialCategories",
                principalColumn: "Nickname");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MaterialTypes_MaterialCategories_MaterialCategoryNickname",
                table: "MaterialTypes");

            migrationBuilder.DropTable(
                name: "PrinterCategories");

            migrationBuilder.DropTable(
                name: "MaterialCategories");

            migrationBuilder.DropIndex(
                name: "IX_MaterialTypes_MaterialCategoryNickname",
                table: "MaterialTypes");

            migrationBuilder.DropColumn(
                name: "MaterialCategoryNickname",
                table: "MaterialTypes");
        }
    }
}
