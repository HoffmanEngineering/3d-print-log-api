using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSpoolmanUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpoolmanUsages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PrintId = table.Column<long>(type: "bigint", nullable: false),
                    FilamentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LengthMm = table.Column<double>(type: "float", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpoolmanUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpoolmanUsages_Prints_PrintId",
                        column: x => x.PrintId,
                        principalTable: "Prints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpoolmanUsages_PrintId_FilamentId",
                table: "SpoolmanUsages",
                columns: new[] { "PrintId", "FilamentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpoolmanUsages");
        }
    }
}
