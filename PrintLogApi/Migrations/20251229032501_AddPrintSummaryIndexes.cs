using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintSummaryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Prints_CreatedById",
                table: "Prints");

            migrationBuilder.DropIndex(
                name: "IX_PrintImages_PrintId",
                table: "PrintImages");

            migrationBuilder.DropIndex(
                name: "IX_PrintFilament_FilamentId",
                table: "PrintFilament");

            migrationBuilder.DropIndex(
                name: "IX_PrintFilament_PrintId",
                table: "PrintFilament");

            migrationBuilder.DropIndex(
                name: "IX_FilamentAdjustments_FilamentId",
                table: "FilamentAdjustments");

            migrationBuilder.CreateIndex(
                name: "IX_Prints_Summary",
                table: "Prints",
                columns: new[] { "CreatedById", "ViewStatus", "StartDate", "CreatedDate" })
                .Annotation("SqlServer:Include", new[] { "Id", "Title", "Status", "PrinterId", "EstimatedPrintTimeInSeconds", "PrintTimeInSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_PrintImages_PrintId_Default",
                table: "PrintImages",
                column: "PrintId",
                filter: "[IsDefault] = 1")
                .Annotation("SqlServer:Include", new[] { "Id", "FileId", "IsDefault", "CreatedDate", "CreatedById", "UpdatedDate", "UpdatedById" });

            migrationBuilder.CreateIndex(
                name: "IX_PrintFilament_FilamentId_Covering",
                table: "PrintFilament",
                column: "FilamentId")
                .Annotation("SqlServer:Include", new[] { "PrintId", "EstimatedAmountMg", "AmountMg", "EstimatedLengthInM", "LengthInM", "EstimatedVolumeMl", "VolumeMl", "EstimatedSource", "Source", "Notes" });

            migrationBuilder.CreateIndex(
                name: "IX_PrintFilament_PrintId",
                table: "PrintFilament",
                column: "PrintId")
                .Annotation("SqlServer:Include", new[] { "FilamentId", "AmountMg", "EstimatedAmountMg", "Notes", "EstimatedLengthInM", "LengthInM", "EstimatedSource", "EstimatedVolumeMl", "Source", "VolumeMl" });

            migrationBuilder.CreateIndex(
                name: "IX_Printers_Id_Covering",
                table: "Printers",
                column: "Id")
                .Annotation("SqlServer:Include", new[] { "UserId", "Make", "Model", "Description", "NozzleDiameter", "FilamentDiameter", "IsActive", "Name", "CategoryNickname", "BeamDiameter", "BedDepthMm", "BedHeightMm", "BedWidthMm", "HasHeatedBed", "HasHeatedChamber", "ScreenResolutionXPixels", "ScreenResolutionYPixels" });

            migrationBuilder.CreateIndex(
                name: "IX_Filaments_Id_Covering",
                table: "Filaments",
                column: "Id")
                .Annotation("SqlServer:Include", new[] { "DisplayName", "Brand", "MaterialType", "MaterialCategoryNickname", "MaterialDensityGramPerCubicCm", "ColorName", "ColorHex", "RecommendedTemp", "IsActive", "Notes", "CreatedDate", "PurchasePriceValue", "InitialNominalWeightMg", "DiameterMm", "StorageLocation", "IsFavorite", "InitialNominalVolumeMl" });

            migrationBuilder.CreateIndex(
                name: "IX_FilamentAdjustments_FilamentId_Covering",
                table: "FilamentAdjustments",
                column: "FilamentId")
                .Annotation("SqlServer:Include", new[] { "AmountMg", "VolumeMl", "LengthInM", "CreatedDate", "Notes" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Prints_Summary",
                table: "Prints");

            migrationBuilder.DropIndex(
                name: "IX_PrintImages_PrintId_Default",
                table: "PrintImages");

            migrationBuilder.DropIndex(
                name: "IX_PrintFilament_FilamentId_Covering",
                table: "PrintFilament");

            migrationBuilder.DropIndex(
                name: "IX_PrintFilament_PrintId",
                table: "PrintFilament");

            migrationBuilder.DropIndex(
                name: "IX_Printers_Id_Covering",
                table: "Printers");

            migrationBuilder.DropIndex(
                name: "IX_Filaments_Id_Covering",
                table: "Filaments");

            migrationBuilder.DropIndex(
                name: "IX_FilamentAdjustments_FilamentId_Covering",
                table: "FilamentAdjustments");

            migrationBuilder.CreateIndex(
                name: "IX_Prints_CreatedById",
                table: "Prints",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_PrintImages_PrintId",
                table: "PrintImages",
                column: "PrintId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintFilament_FilamentId",
                table: "PrintFilament",
                column: "FilamentId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintFilament_PrintId",
                table: "PrintFilament",
                column: "PrintId")
                .Annotation("SqlServer:Include", new[] { "FilamentId", "AmountMg", "EstimatedAmountMg" });

            migrationBuilder.CreateIndex(
                name: "IX_FilamentAdjustments_FilamentId",
                table: "FilamentAdjustments",
                column: "FilamentId");
        }
    }
}
