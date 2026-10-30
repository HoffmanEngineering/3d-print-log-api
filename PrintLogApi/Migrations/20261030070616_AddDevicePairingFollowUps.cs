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
            migrationBuilder.AddColumn<DateTime>(
                name: "PairingNoticeAt",
                table: "DeviceGrants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PairingNoticeFor",
                table: "DeviceGrants",
                type: "nvarchar(64)",
                maxLength: 64,
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
                name: "IX_DeviceGrants_PreviousTokenHash",
                table: "DeviceGrants",
                column: "PreviousTokenHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeviceGrants_PreviousTokenHash",
                table: "DeviceGrants");

            migrationBuilder.DropColumn(
                name: "PairingNoticeAt",
                table: "DeviceGrants");

            migrationBuilder.DropColumn(
                name: "PairingNoticeFor",
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
