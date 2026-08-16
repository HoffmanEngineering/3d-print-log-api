using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddFilamentMaterialCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasDiameter",
                table: "MaterialCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowBedTemperature",
                table: "MaterialCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowInertGas",
                table: "MaterialCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowMaterialRefreshRatio",
                table: "MaterialCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowMeltingTemperature",
                table: "MaterialCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowNozzleTemperature",
                table: "MaterialCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowRecommendedInitialLayerTimeInSeconds",
                table: "MaterialCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowRecommendedLayerTimeInSeconds",
                table: "MaterialCategories",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MaterialCategoryNickname",
                table: "Filaments",
                type: "nvarchar(50)",
                nullable: true,
                defaultValue: "filament");

            migrationBuilder.UpdateData(
                table: "MaterialCategories",
                keyColumn: "Nickname",
                keyValue: "filament",
                columns: new[] { "HasDiameter", "ShowBedTemperature", "ShowInertGas", "ShowMaterialRefreshRatio", "ShowMeltingTemperature", "ShowNozzleTemperature", "ShowRecommendedInitialLayerTimeInSeconds", "ShowRecommendedLayerTimeInSeconds" },
                values: new object[] { true, true, false, false, false, true, false, false });

            migrationBuilder.UpdateData(
                table: "MaterialCategories",
                keyColumn: "Nickname",
                keyValue: "powder",
                columns: new[] { "HasDiameter", "ShowBedTemperature", "ShowInertGas", "ShowMaterialRefreshRatio", "ShowMeltingTemperature", "ShowNozzleTemperature", "ShowRecommendedInitialLayerTimeInSeconds", "ShowRecommendedLayerTimeInSeconds" },
                values: new object[] { false, false, true, true, true, false, false, false });

            migrationBuilder.UpdateData(
                table: "MaterialCategories",
                keyColumn: "Nickname",
                keyValue: "resin",
                columns: new[] { "HasDiameter", "ShowBedTemperature", "ShowInertGas", "ShowMaterialRefreshRatio", "ShowMeltingTemperature", "ShowNozzleTemperature", "ShowRecommendedInitialLayerTimeInSeconds", "ShowRecommendedLayerTimeInSeconds" },
                values: new object[] { false, false, false, false, false, false, true, true });

            migrationBuilder.UpdateData(
                table: "MaterialCategories",
                keyColumn: "Nickname",
                keyValue: "wire",
                columns: new[] { "HasDiameter", "ShowBedTemperature", "ShowInertGas", "ShowMaterialRefreshRatio", "ShowMeltingTemperature", "ShowNozzleTemperature", "ShowRecommendedInitialLayerTimeInSeconds", "ShowRecommendedLayerTimeInSeconds" },
                values: new object[] { true, true, false, false, false, true, false, false });

            migrationBuilder.CreateIndex(
                name: "IX_Filaments_MaterialCategoryNickname",
                table: "Filaments",
                column: "MaterialCategoryNickname");

            migrationBuilder.AddForeignKey(
                name: "FK_Filaments_MaterialCategories_MaterialCategoryNickname",
                table: "Filaments",
                column: "MaterialCategoryNickname",
                principalTable: "MaterialCategories",
                principalColumn: "Nickname");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Filaments_MaterialCategories_MaterialCategoryNickname",
                table: "Filaments");

            migrationBuilder.DropIndex(
                name: "IX_Filaments_MaterialCategoryNickname",
                table: "Filaments");

            migrationBuilder.DropColumn(
                name: "HasDiameter",
                table: "MaterialCategories");

            migrationBuilder.DropColumn(
                name: "ShowBedTemperature",
                table: "MaterialCategories");

            migrationBuilder.DropColumn(
                name: "ShowInertGas",
                table: "MaterialCategories");

            migrationBuilder.DropColumn(
                name: "ShowMaterialRefreshRatio",
                table: "MaterialCategories");

            migrationBuilder.DropColumn(
                name: "ShowMeltingTemperature",
                table: "MaterialCategories");

            migrationBuilder.DropColumn(
                name: "ShowNozzleTemperature",
                table: "MaterialCategories");

            migrationBuilder.DropColumn(
                name: "ShowRecommendedInitialLayerTimeInSeconds",
                table: "MaterialCategories");

            migrationBuilder.DropColumn(
                name: "ShowRecommendedLayerTimeInSeconds",
                table: "MaterialCategories");

            migrationBuilder.DropColumn(
                name: "MaterialCategoryNickname",
                table: "Filaments");
        }
    }
}
