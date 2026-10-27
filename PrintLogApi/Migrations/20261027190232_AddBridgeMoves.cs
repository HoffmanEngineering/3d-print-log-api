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
            migrationBuilder.AddColumn<DateTime>(
                name: "LastSharedJobAt",
                table: "Bridges",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastSharedJobPrinterId",
                table: "Bridges",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SharedJobCount",
                table: "Bridges",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "MovedAt",
                table: "BridgePrinters",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BridgeSharedJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BridgeId = table.Column<long>(type: "bigint", nullable: false),
                    ExternalSource = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReportedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BridgeSharedJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BridgeSharedJobs_Bridges_BridgeId",
                        column: x => x.BridgeId,
                        principalTable: "Bridges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BridgeSharedJobs_BridgeId_ExternalSource_ExternalId",
                table: "BridgeSharedJobs",
                columns: new[] { "BridgeId", "ExternalSource", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BridgeSharedJobs");

            migrationBuilder.DropColumn(
                name: "LastSharedJobAt",
                table: "Bridges");

            migrationBuilder.DropColumn(
                name: "LastSharedJobPrinterId",
                table: "Bridges");

            migrationBuilder.DropColumn(
                name: "SharedJobCount",
                table: "Bridges");

            migrationBuilder.DropColumn(
                name: "MovedAt",
                table: "BridgePrinters");
        }
    }
}
