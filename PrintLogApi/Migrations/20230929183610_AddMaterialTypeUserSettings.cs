using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddMaterialTypeUserSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Printers_PrinterCategories_typeNickname",
                table: "Printers");

            migrationBuilder.RenameColumn(
                name: "typeNickname",
                table: "Printers",
                newName: "TypeNickname");

            migrationBuilder.RenameIndex(
                name: "IX_Printers_typeNickname",
                table: "Printers",
                newName: "IX_Printers_TypeNickname");

            migrationBuilder.InsertData(
                table: "UserSettingTypes",
                columns: new[] { "Id", "Description", "Name" },
                values: new object[,]
                {
                    { 9, "The last selected resin measure type on the print.", "Prints_LastSelectedResinMeasureType" },
                    { 10, "The last selected powder measure type on the print.", "Prints_LastSelectedPowderMeasureType" },
                    { 11, "The last selected wire measure type on the print.", "Prints_LastSelectedWireMeasureType" }
                });

            migrationBuilder.AddForeignKey(
                name: "FK_Printers_PrinterCategories_TypeNickname",
                table: "Printers",
                column: "TypeNickname",
                principalTable: "PrinterCategories",
                principalColumn: "Nickname");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Printers_PrinterCategories_TypeNickname",
                table: "Printers");

            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 9);

            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 10);

            migrationBuilder.DeleteData(
                table: "UserSettingTypes",
                keyColumn: "Id",
                keyValue: 11);

            migrationBuilder.RenameColumn(
                name: "TypeNickname",
                table: "Printers",
                newName: "typeNickname");

            migrationBuilder.RenameIndex(
                name: "IX_Printers_TypeNickname",
                table: "Printers",
                newName: "IX_Printers_typeNickname");

            migrationBuilder.AddForeignKey(
                name: "FK_Printers_PrinterCategories_typeNickname",
                table: "Printers",
                column: "typeNickname",
                principalTable: "PrinterCategories",
                principalColumn: "Nickname");
        }
    }
}
