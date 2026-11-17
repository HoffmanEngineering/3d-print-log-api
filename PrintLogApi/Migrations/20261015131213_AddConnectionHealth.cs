using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ErrorCount",
                table: "Connections",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KlipperVersion",
                table: "Connections",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "Connections",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastErrorAt",
                table: "Connections",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "Connections",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MoonrakerVersion",
                table: "Connections",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutboxDepth",
                table: "Connections",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OutboxOldestQueuedAt",
                table: "Connections",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrinterModel",
                table: "Connections",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SlicerSettings",
                table: "Connections",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Snapshots",
                table: "Connections",
                type: "bit",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ErrorCount",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "KlipperVersion",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "LastErrorAt",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "MoonrakerVersion",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "OutboxDepth",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "OutboxOldestQueuedAt",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "PrinterModel",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "SlicerSettings",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "Snapshots",
                table: "Connections");
        }
    }
}
