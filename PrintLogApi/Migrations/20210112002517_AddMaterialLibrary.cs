using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class AddMaterialLibrary : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Materials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Acronym = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    DensityGramPerCubicCm = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Materials", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Materials",
                columns: new[] { "Id", "Acronym", "DensityGramPerCubicCm", "Name" },
                values: new object[,]
                {
                    { new Guid("c78c56d0-b34d-49b1-849e-a54066a2f5e3"), "ABS", 1.1000000000000001, "Acrylonitrile Butadiene Styrene" },
                    { new Guid("c8cae1e0-5f13-41d6-9f72-cb83740aa2fe"), "CPE", 1.27, "Co-polyester" },
                    { new Guid("b0cda842-5a48-4a30-a060-226680e13c06"), "HIPS", 1.24, "High Impact Polystyrene" },
                    { new Guid("3dbc49c5-a493-4e21-a4d5-d94b8c0d53da"), null, 1.1399999999999999, "Nylon" },
                    { new Guid("7d83cbc1-00d0-4e42-a7ce-8a1b831b175b"), "PC", 1.1899999999999999, "Polycarbonate" },
                    { new Guid("35151bfe-6890-41ab-8fc9-443c5a690626"), "PCTG", 1.24, "Cyclohexylenedimethylene Terephthalate Glycol" },
                    { new Guid("23e38c0d-43f3-4bcd-b3c6-830d193a3e10"), "PETG", 1.3799999999999999, "Polyethylene Terephthalate Glycol" },
                    { new Guid("f8a6b364-94a6-4a32-a253-e67b41df1969"), "PLA", 1.24, "Polylactic Acid" },
                    { new Guid("0c236829-8487-4bb4-a092-68a9731a64e4"), "PVA", 1.23, "Polyvinyl Acetate" },
                    { new Guid("d777bde9-fba6-4f5a-b7a4-e8a4a9695715"), "TPU 95A", 1.22, "Thermoplastic Polyurethane" }
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Materials");
        }
    }
}
