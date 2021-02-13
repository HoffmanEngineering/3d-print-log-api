using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class MakeFilamentOptionalForPrintFilament : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PrintFilament_Filaments_FilamentId",
                table: "PrintFilament");

            migrationBuilder.AlterColumn<Guid>(
                name: "FilamentId",
                table: "PrintFilament",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddForeignKey(
                name: "FK_PrintFilament_Filaments_FilamentId",
                table: "PrintFilament",
                column: "FilamentId",
                principalTable: "Filaments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PrintFilament_Filaments_FilamentId",
                table: "PrintFilament");

            migrationBuilder.AlterColumn<Guid>(
                name: "FilamentId",
                table: "PrintFilament",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PrintFilament_Filaments_FilamentId",
                table: "PrintFilament",
                column: "FilamentId",
                principalTable: "Filaments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
