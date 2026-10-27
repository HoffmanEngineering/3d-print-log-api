using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBridges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BridgeId",
                table: "Connections",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Bridges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AgentVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    HostName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Networks = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DesiredRevision = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScanRequestedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSweepAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bridges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bridges_DeviceGrants_Id",
                        column: x => x.Id,
                        principalTable: "DeviceGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BridgeDiscoveredPrinters",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BridgeId = table.Column<long>(type: "bigint", nullable: false),
                    IdentityKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: true),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Serial = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MainboardId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Macs = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Configured = table.Column<bool>(type: "bit", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BridgeDiscoveredPrinters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BridgeDiscoveredPrinters_Bridges_BridgeId",
                        column: x => x.BridgeId,
                        principalTable: "Bridges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BridgeIgnoredPrinters",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BridgeId = table.Column<long>(type: "bigint", nullable: false),
                    IdentityKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BridgeIgnoredPrinters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BridgeIgnoredPrinters_Bridges_BridgeId",
                        column: x => x.BridgeId,
                        principalTable: "Bridges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BridgePrinters",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BridgeId = table.Column<long>(type: "bigint", nullable: false),
                    PrinterId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Host = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: true),
                    Serial = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IdentityKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Macs = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BridgePrinters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BridgePrinters_Bridges_BridgeId",
                        column: x => x.BridgeId,
                        principalTable: "Bridges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BridgePrinters_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Connections_BridgeId",
                table: "Connections",
                column: "BridgeId");

            migrationBuilder.CreateIndex(
                name: "IX_BridgeDiscoveredPrinters_BridgeId_IdentityKey",
                table: "BridgeDiscoveredPrinters",
                columns: new[] { "BridgeId", "IdentityKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BridgeIgnoredPrinters_BridgeId_IdentityKey",
                table: "BridgeIgnoredPrinters",
                columns: new[] { "BridgeId", "IdentityKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BridgePrinters_BridgeId_IdentityKey",
                table: "BridgePrinters",
                columns: new[] { "BridgeId", "IdentityKey" },
                unique: true,
                filter: "[IdentityKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BridgePrinters_BridgeId_PrinterId",
                table: "BridgePrinters",
                columns: new[] { "BridgeId", "PrinterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BridgePrinters_PrinterId",
                table: "BridgePrinters",
                column: "PrinterId");

            migrationBuilder.AddForeignKey(
                name: "FK_Connections_Bridges_BridgeId",
                table: "Connections",
                column: "BridgeId",
                principalTable: "Bridges",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Connections_Bridges_BridgeId",
                table: "Connections");

            migrationBuilder.DropTable(
                name: "BridgeDiscoveredPrinters");

            migrationBuilder.DropTable(
                name: "BridgeIgnoredPrinters");

            migrationBuilder.DropTable(
                name: "BridgePrinters");

            migrationBuilder.DropTable(
                name: "Bridges");

            migrationBuilder.DropIndex(
                name: "IX_Connections_BridgeId",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "BridgeId",
                table: "Connections");
        }
    }
}
