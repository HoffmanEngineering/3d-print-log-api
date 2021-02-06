using Microsoft.EntityFrameworkCore.Migrations;

namespace PrintLogApi.Migrations
{
    public partial class AddLengthPropertiesToFilamentUsage : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "EstimatedLengthInM",
                table: "PrintFilament",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LengthInM",
                table: "PrintFilament",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LengthIsSource",
                table: "PrintFilament",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Convert amounts in weight to amounts in length
            migrationBuilder.Sql(@"
/* Convert Estimated Amounts to Estimated Lengths in meter */
UPDATE pf
SET EstimatedLengthInM = (EstimatedAmountMg / (250 * pi() * filaments.MaterialDensityGramPerCubicCm * filaments.DiameterMm * filaments.DiameterMm))
FROM PrintFilament pf
INNER JOIN Filaments ON pf.FilamentId = Filaments.Id
WHERE EstimatedLengthInM Is Null 
	AND DiameterMm > 0
	AND MaterialDensityGramPerCubicCm > 0
");

            migrationBuilder.Sql(@"
/* Convert Amounts to Estimated Lengths in meter */
UPDATE pf
SET LengthInM = (AmountMg / (250 * pi() * filaments.MaterialDensityGramPerCubicCm * filaments.DiameterMm * filaments.DiameterMm))
FROM PrintFilament pf
INNER JOIN Filaments ON pf.FilamentId = Filaments.Id
WHERE LengthInM Is Null
	AND DiameterMm > 0
	AND MaterialDensityGramPerCubicCm > 0
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EstimatedLengthInM",
                table: "PrintFilament");

            migrationBuilder.DropColumn(
                name: "LengthInM",
                table: "PrintFilament");

            migrationBuilder.DropColumn(
                name: "LengthIsSource",
                table: "PrintFilament");
        }
    }
}
