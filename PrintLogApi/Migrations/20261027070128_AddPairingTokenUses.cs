using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPairingTokenUses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PairingTokenUses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TokenKeyHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairingTokenUses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PairingTokenUses_ExpiresAt",
                table: "PairingTokenUses",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PairingTokenUses_TokenKeyHash",
                table: "PairingTokenUses",
                column: "TokenKeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PairingTokenUses");
        }
    }
}
