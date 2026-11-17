using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCuraSettingPrintId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PrintId",
                table: "CuraSettings",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CuraSettings_PrintId",
                table: "CuraSettings",
                column: "PrintId",
                unique: true,
                filter: "[PrintId] IS NOT NULL");

            // Cascade is safe on SQL Server (no error 1785): CuraSettings has no other foreign key,
            // so this is the only cascade path into it, below the single one that reaches Prints.
            migrationBuilder.AddForeignKey(
                name: "FK_CuraSettings_Prints_PrintId",
                table: "CuraSettings",
                column: "PrintId",
                principalTable: "Prints",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CuraSettings_Prints_PrintId",
                table: "CuraSettings");

            migrationBuilder.DropIndex(
                name: "IX_CuraSettings_PrintId",
                table: "CuraSettings");

            migrationBuilder.DropColumn(
                name: "PrintId",
                table: "CuraSettings");
        }
    }
}
