using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class ChangeDefaultMaterialCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Filaments_MaterialCategories_MaterialCategoryNickname",
                table: "Filaments");

            migrationBuilder.AlterColumn<string>(
                name: "MaterialCategoryNickname",
                table: "Filaments",
                type: "nvarchar(50)",
                nullable: false,
                defaultValue: "filament",
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Filaments_MaterialCategories_MaterialCategoryNickname",
                table: "Filaments",
                column: "MaterialCategoryNickname",
                principalTable: "MaterialCategories",
                principalColumn: "Nickname",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Filaments_MaterialCategories_MaterialCategoryNickname",
                table: "Filaments");

            migrationBuilder.AlterColumn<string>(
                name: "MaterialCategoryNickname",
                table: "Filaments",
                type: "nvarchar(50)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldDefaultValue: "filament");

            migrationBuilder.AddForeignKey(
                name: "FK_Filaments_MaterialCategories_MaterialCategoryNickname",
                table: "Filaments",
                column: "MaterialCategoryNickname",
                principalTable: "MaterialCategories",
                principalColumn: "Nickname");
        }
    }
}
