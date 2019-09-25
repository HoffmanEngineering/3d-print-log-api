using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class AddTimestampToPrint : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CreatedById",
                table: "Prints",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedDate",
                table: "Prints",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<long>(
                name: "UpdatedById",
                table: "Prints",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedDate",
                table: "Prints",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_Prints_CreatedById",
                table: "Prints",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Prints_UpdatedById",
                table: "Prints",
                column: "UpdatedById");

            migrationBuilder.AddForeignKey(
                name: "FK_Prints_Users_CreatedById",
                table: "Prints",
                column: "CreatedById",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Prints_Users_UpdatedById",
                table: "Prints",
                column: "UpdatedById",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Prints_Users_CreatedById",
                table: "Prints");

            migrationBuilder.DropForeignKey(
                name: "FK_Prints_Users_UpdatedById",
                table: "Prints");

            migrationBuilder.DropIndex(
                name: "IX_Prints_CreatedById",
                table: "Prints");

            migrationBuilder.DropIndex(
                name: "IX_Prints_UpdatedById",
                table: "Prints");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "Prints");

            migrationBuilder.DropColumn(
                name: "CreatedDate",
                table: "Prints");

            migrationBuilder.DropColumn(
                name: "UpdatedById",
                table: "Prints");

            migrationBuilder.DropColumn(
                name: "UpdatedDate",
                table: "Prints");
        }
    }
}
