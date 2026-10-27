using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBridgeMoves : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DroppedEventCount",
                table: "Bridges",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastDroppedEventAt",
                table: "Bridges",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastDroppedPrinterId",
                table: "Bridges",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MovedAt",
                table: "BridgePrinters",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DroppedEventCount",
                table: "Bridges");

            migrationBuilder.DropColumn(
                name: "LastDroppedEventAt",
                table: "Bridges");

            migrationBuilder.DropColumn(
                name: "LastDroppedPrinterId",
                table: "Bridges");

            migrationBuilder.DropColumn(
                name: "MovedAt",
                table: "BridgePrinters");
        }
    }
}
