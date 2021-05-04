using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class AddResolvedDateAndNoteToFeedback : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolvedDate",
                table: "Feedback",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedNote",
                table: "Feedback",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResolvedDate",
                table: "Feedback");

            migrationBuilder.DropColumn(
                name: "ResolvedNote",
                table: "Feedback");
        }
    }
}
