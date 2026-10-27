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

            migrationBuilder.AddColumn<long>(
                name: "MovedFromBridgeId",
                table: "BridgePrinters",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BridgeDroppedJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BridgeId = table.Column<long>(type: "bigint", nullable: false),
                    ExternalSource = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DroppedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BridgeDroppedJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BridgeDroppedJobs_Bridges_BridgeId",
                        column: x => x.BridgeId,
                        principalTable: "Bridges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BridgeDroppedJobs_BridgeId_ExternalSource_ExternalId",
                table: "BridgeDroppedJobs",
                columns: new[] { "BridgeId", "ExternalSource", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BridgeDroppedJobs");

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

            migrationBuilder.DropColumn(
                name: "MovedFromBridgeId",
                table: "BridgePrinters");
        }
    }
}
