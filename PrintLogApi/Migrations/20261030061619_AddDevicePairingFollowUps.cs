using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddDevicePairingFollowUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "GrantId",
                table: "PairingTokenUses",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PairingNoticeAt",
                table: "DeviceGrants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PreviousTokenExpiresAt",
                table: "DeviceGrants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousTokenHash",
                table: "DeviceGrants",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PairingTokenUses_GrantId",
                table: "PairingTokenUses",
                column: "GrantId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGrants_PreviousTokenHash",
                table: "DeviceGrants",
                column: "PreviousTokenHash");

            migrationBuilder.AddForeignKey(
                name: "FK_PairingTokenUses_DeviceGrants_GrantId",
                table: "PairingTokenUses",
                column: "GrantId",
                principalTable: "DeviceGrants",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PairingTokenUses_DeviceGrants_GrantId",
                table: "PairingTokenUses");

            migrationBuilder.DropIndex(
                name: "IX_PairingTokenUses_GrantId",
                table: "PairingTokenUses");

            migrationBuilder.DropIndex(
                name: "IX_DeviceGrants_PreviousTokenHash",
                table: "DeviceGrants");

            migrationBuilder.DropColumn(
                name: "GrantId",
                table: "PairingTokenUses");

            migrationBuilder.DropColumn(
                name: "PairingNoticeAt",
                table: "DeviceGrants");

            migrationBuilder.DropColumn(
                name: "PreviousTokenExpiresAt",
                table: "DeviceGrants");

            migrationBuilder.DropColumn(
                name: "PreviousTokenHash",
                table: "DeviceGrants");
        }
    }
}
