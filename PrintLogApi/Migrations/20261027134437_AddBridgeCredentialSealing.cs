using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBridgeCredentialSealing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "KeyChangesDay",
                table: "Bridges",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KeyChangesToday",
                table: "Bridges",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PendingPublicKey",
                table: "Bridges",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PendingPublicKeyAt",
                table: "Bridges",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingPublicKeyFingerprint",
                table: "Bridges",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicKey",
                table: "Bridges",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublicKeyChangedAt",
                table: "Bridges",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicKeyFingerprint",
                table: "Bridges",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CredentialIssues",
                table: "BridgePrinters",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IssuesStamp",
                table: "BridgePrinters",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "BridgePrinterSecrets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BridgePrinterId = table.Column<long>(type: "bigint", nullable: false),
                    Field = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SealedValue = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    KeyFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BridgePrinterSecrets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BridgePrinterSecrets_BridgePrinters_BridgePrinterId",
                        column: x => x.BridgePrinterId,
                        principalTable: "BridgePrinters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BridgePrinterSecrets_BridgePrinterId_Field",
                table: "BridgePrinterSecrets",
                columns: new[] { "BridgePrinterId", "Field" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BridgePrinterSecrets");

            migrationBuilder.DropColumn(
                name: "KeyChangesDay",
                table: "Bridges");

            migrationBuilder.DropColumn(
                name: "KeyChangesToday",
                table: "Bridges");

            migrationBuilder.DropColumn(
                name: "PendingPublicKey",
                table: "Bridges");

            migrationBuilder.DropColumn(
                name: "PendingPublicKeyAt",
                table: "Bridges");

            migrationBuilder.DropColumn(
                name: "PendingPublicKeyFingerprint",
                table: "Bridges");

            migrationBuilder.DropColumn(
                name: "PublicKey",
                table: "Bridges");

            migrationBuilder.DropColumn(
                name: "PublicKeyChangedAt",
                table: "Bridges");

            migrationBuilder.DropColumn(
                name: "PublicKeyFingerprint",
                table: "Bridges");

            migrationBuilder.DropColumn(
                name: "CredentialIssues",
                table: "BridgePrinters");

            migrationBuilder.DropColumn(
                name: "IssuesStamp",
                table: "BridgePrinters");
        }
    }
}
