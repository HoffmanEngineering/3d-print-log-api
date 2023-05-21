using Microsoft.EntityFrameworkCore.Migrations;
using PrintLogApi.Models;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintFilamentVolume : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

            // Handle migration of the LengthIsSource fields to the new Source enums
            migrationBuilder.AddColumn<int>(
                name: "EstimatedSource",
                table: "PrintFilament",
                type: "int",
                nullable: false,
                defaultValue: PrintFilament.SourceMeasurement.Weight);

            migrationBuilder.AddColumn<double>(
                name: "EstimatedVolumeMl",
                table: "PrintFilament",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "PrintFilament",
                type: "int",
                nullable: false,
                defaultValue: PrintFilament.SourceMeasurement.Weight);

            migrationBuilder.AddColumn<double>(
                name: "VolumeMl",
                table: "PrintFilament",
                type: "float",
                nullable: true);

            // Convert the existing Source columns
            migrationBuilder.Sql($@"
UPDATE PrintFilament
SET EstimatedSource = {PrintFilament.SourceMeasurement.Weight}
WHERE IsEstimatedLengthSource = {false}
");

            migrationBuilder.Sql($@"
UPDATE PrintFilament
SET EstimatedSource = {PrintFilament.SourceMeasurement.Length}
WHERE IsEstimatedLengthSource = {true}
");

            migrationBuilder.Sql($@"
UPDATE PrintFilament
SET Source = {PrintFilament.SourceMeasurement.Weight}
WHERE IsActualLengthSource = {false}
");

            migrationBuilder.Sql($@"
UPDATE PrintFilament
SET Source = {PrintFilament.SourceMeasurement.Length}
WHERE IsActualLengthSource = {true}
");


            // Now calculate the volumn fields
            migrationBuilder.Sql(@"
/* Convert Estimated Lengths to Estimated Volumes */
UPDATE pf
SET EstimatedVolumeMl = CAST((1/4.00) * PI() * pf.EstimatedLengthInM * (DiameterMm) * (DiameterMm) as float)
FROM PrintFilament pf
INNER JOIN Filaments ON pf.FilamentId = Filaments.Id
WHERE 
(pf.EstimatedLengthInM is not null and pf.EstimatedAmountMg is NULL)
	AND DiameterMm > 0;
");

            migrationBuilder.Sql(@"
/* Convert Actual Lengths to Volumes */
UPDATE pf
SET VolumeMl = CAST((1/4.00) * PI() * pf.LengthInM * (DiameterMm) * (DiameterMm) as float)
FROM PrintFilament pf
INNER JOIN Filaments ON pf.FilamentId = Filaments.Id
WHERE 
(pf.LengthInM is not null)
	AND DiameterMm > 0;
");


            migrationBuilder.DropColumn(
                name: "IsActualLengthSource",
                table: "PrintFilament");

            migrationBuilder.DropColumn(
                name: "IsEstimatedLengthSource",
                table: "PrintFilament");




            migrationBuilder.AddColumn<string>(
                name: "typeNickname",
                table: "Printers",
                type: "nvarchar(50)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Printers_typeNickname",
                table: "Printers",
                column: "typeNickname");

            migrationBuilder.AddForeignKey(
                name: "FK_Printers_PrinterCategories_typeNickname",
                table: "Printers",
                column: "typeNickname",
                principalTable: "PrinterCategories",
                principalColumn: "Nickname");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Printers_PrinterCategories_typeNickname",
                table: "Printers");

            migrationBuilder.DropIndex(
                name: "IX_Printers_typeNickname",
                table: "Printers");

            migrationBuilder.DropColumn(
                name: "EstimatedVolumeMl",
                table: "PrintFilament");

            migrationBuilder.DropColumn(
                name: "VolumeMl",
                table: "PrintFilament");

            migrationBuilder.DropColumn(
                name: "typeNickname",
                table: "Printers");

            migrationBuilder.AddColumn<bool>(
                name: "IsActualLengthSource",
                table: "PrintFilament",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsEstimatedLengthSource",
                table: "PrintFilament",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Convert the existing Source columns
            migrationBuilder.Sql($@"
UPDATE PrintFilament
SET IsEstimatedLengthSource = {true}
WHERE EstimatedSource = {PrintFilament.SourceMeasurement.Length}
");

            migrationBuilder.Sql($@"
UPDATE PrintFilament
SET IsActualLengthSource = {true}
WHERE Source = {PrintFilament.SourceMeasurement.Length}
");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "PrintFilament");
            migrationBuilder.DropColumn(
                name: "EstimatedSource",
                table: "PrintFilament");
        }
    }
}
