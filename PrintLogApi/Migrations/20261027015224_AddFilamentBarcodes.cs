using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddFilamentBarcodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FilamentBarcodes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FilamentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Format = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilamentBarcodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FilamentBarcodes_Filaments_FilamentId",
                        column: x => x.FilamentId,
                        principalTable: "Filaments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FilamentBarcodes_FilamentId_Value",
                table: "FilamentBarcodes",
                columns: new[] { "FilamentId", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FilamentBarcodes_Value",
                table: "FilamentBarcodes",
                column: "Value");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FilamentBarcodes");
        }
    }
}
