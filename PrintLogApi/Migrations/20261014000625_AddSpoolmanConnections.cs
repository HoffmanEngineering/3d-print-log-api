using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSpoolmanConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpoolmanConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    PrinterId = table.Column<long>(type: "bigint", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SecretHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    HashAlgorithm = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DisabledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DisabledReason = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpoolmanConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpoolmanConnections_Printers_PrinterId",
                        column: x => x.PrinterId,
                        principalTable: "Printers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SpoolmanConnections_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpoolmanFairUseAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Signal = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LastSentAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpoolmanFairUseAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpoolmanFairUseAlerts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpoolmanNetworkSightings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    NetworkHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpoolmanNetworkSightings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpoolmanNetworkSightings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpoolmanConnections_PrinterId",
                table: "SpoolmanConnections",
                column: "PrinterId",
                unique: true,
                filter: "[PrinterId] IS NOT NULL AND [RevokedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SpoolmanConnections_SecretHash",
                table: "SpoolmanConnections",
                column: "SecretHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpoolmanConnections_UserId_CreatedDate",
                table: "SpoolmanConnections",
                columns: new[] { "UserId", "CreatedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SpoolmanFairUseAlerts_UserId_Signal",
                table: "SpoolmanFairUseAlerts",
                columns: new[] { "UserId", "Signal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpoolmanNetworkSightings_UserId_NetworkHash",
                table: "SpoolmanNetworkSightings",
                columns: new[] { "UserId", "NetworkHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpoolmanConnections");

            migrationBuilder.DropTable(
                name: "SpoolmanFairUseAlerts");

            migrationBuilder.DropTable(
                name: "SpoolmanNetworkSightings");
        }
    }
}
