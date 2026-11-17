using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionNotifierDrops : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DroppedNotifierEventCount",
                table: "Connections",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastDroppedNotifierEventAt",
                table: "Connections",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NotifierNoticeDismissedAt",
                table: "Connections",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DroppedNotifierEventCount",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "LastDroppedNotifierEventAt",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "NotifierNoticeDismissedAt",
                table: "Connections");
        }
    }
}
