using Microsoft.EntityFrameworkCore.Migrations;
using PrintLogApi.Models;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddFilamentAdjustmentVolume : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "AmountMg",
                table: "FilamentAdjustments",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<double>(
                name: "InitialNominalVolumeMl",
                table: "Filaments",
                type: "float",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<double>(
                name: "InitialNominalLengthM",
                table: "Filaments",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LengthInM",
                table: "FilamentAdjustments",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "FilamentAdjustments",
                type: "int",
                nullable: false,
                defaultValue: FilamentAdjustment.SourceMeasurement.Weight);

            migrationBuilder.AddColumn<double>(
                name: "VolumeMl",
                table: "FilamentAdjustments",
                type: "float",
                nullable: true);

            migrationBuilder.Sql(@"
/* Convert Amounts to Lengths in meter */
UPDATE Filaments
SET InitialNominalLengthM = CAST((InitialNominalWeightMg / (250 * pi() * MaterialDensityGramPerCubicCm * DiameterMm * DiameterMm)) as float)
WHERE InitialNominalWeightMg is not NULL AND DiameterMm > 0 AND MaterialDensityGramPerCubicCm > 0
");

            migrationBuilder.Sql(@"
/* Convert Actual Lengths to Volumes */
UPDATE Filaments
SET InitialNominalVolumeMl = CAST((1/4.00) * PI() * InitialNominalLengthM * (DiameterMm) * (DiameterMm) as float)
WHERE InitialNominalLengthM is not NULL AND DiameterMm > 0 AND MaterialDensityGramPerCubicCm > 0
");

            migrationBuilder.Sql(@"
UPDATE fa
SET LengthInM = (AmountMg / (250 * pi() * filaments.MaterialDensityGramPerCubicCm * filaments.DiameterMm * filaments.DiameterMm))
FROM FilamentAdjustments fa
INNER JOIN Filaments ON fa.FilamentId = Filaments.Id
WHERE AmountMg Is Null 
	AND DiameterMm > 0
	AND MaterialDensityGramPerCubicCm > 0
");

            migrationBuilder.Sql(@"
UPDATE fa
SET fa.VolumeMl = CAST((1/4.00) * PI() * fa.LengthInM * (DiameterMm) * (DiameterMm) as float)
FROM FilamentAdjustments fa
INNER JOIN Filaments ON fa.FilamentId = Filaments.Id
WHERE 
(fa.LengthInM is not null)
	AND Filaments.DiameterMm > 0;
");


        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InitialNominalLengthM",
                table: "Filaments");

            migrationBuilder.DropColumn(
                name: "LengthInM",
                table: "FilamentAdjustments");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "FilamentAdjustments");

            migrationBuilder.DropColumn(
                name: "VolumeMl",
                table: "FilamentAdjustments");

            migrationBuilder.AlterColumn<long>(
                name: "InitialNominalVolumeMl",
                table: "Filaments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(double),
                oldType: "float",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "AmountMg",
                table: "FilamentAdjustments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);
        }
    }
}
