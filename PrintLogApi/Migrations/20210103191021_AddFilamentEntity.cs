using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class AddFilamentEntity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Filaments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Brand = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    MaterialType = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    MaterialDensityGramPerCubicCm = table.Column<double>(type: "float", nullable: false),
                    ColorName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ColorHex = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: true),
                    DiameterMm = table.Column<double>(type: "float", nullable: true),
                    InitialTotalWeightMg = table.Column<long>(type: "bigint", nullable: true),
                    InitialNominalWeightMg = table.Column<long>(type: "bigint", nullable: true),
                    SpoolWeightMg = table.Column<long>(type: "bigint", nullable: true),
                    TempRangeStart = table.Column<double>(type: "float", nullable: true),
                    TempRangeEnd = table.Column<double>(type: "float", nullable: true),
                    RecommendedTemp = table.Column<double>(type: "float", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    PurchaseDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PurchaseLocation = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PurchasePriceValue = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PurchasePriceCurrency = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedById = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedById = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Filaments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Filaments_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_Filaments_Users_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Filaments_CreatedById",
                table: "Filaments",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Filaments_UpdatedById",
                table: "Filaments",
                column: "UpdatedById");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Filaments");
        }
    }
}
