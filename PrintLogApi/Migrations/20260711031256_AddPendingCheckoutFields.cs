using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingCheckoutFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PendingCheckoutExpiresAt",
                table: "Subscriptions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingCheckoutIdempotencyKey",
                table: "Subscriptions",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingCheckoutPlanId",
                table: "Subscriptions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingCheckoutSessionId",
                table: "Subscriptions",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingCheckoutSessionUrl",
                table: "Subscriptions",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingCheckoutExpiresAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "PendingCheckoutIdempotencyKey",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "PendingCheckoutPlanId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "PendingCheckoutSessionId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "PendingCheckoutSessionUrl",
                table: "Subscriptions");
        }
    }
}
