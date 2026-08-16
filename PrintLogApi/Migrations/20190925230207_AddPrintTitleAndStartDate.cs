using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class AddPrintTitleAndStartDate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartDate",
                table: "Prints",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Prints",
                maxLength: 100,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "Prints");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Prints");
        }
    }
}
