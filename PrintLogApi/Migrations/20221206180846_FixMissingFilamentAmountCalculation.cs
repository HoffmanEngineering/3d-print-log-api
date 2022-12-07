using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class FixMissingFilamentAmountCalculation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fix bug with octoprint where lengths were not being used to calculate the amounts, rendering filament tracking useless.
            migrationBuilder.Sql(@"
/* Convert Estimated Lengths to Estimated Amounts */
UPDATE pf
SET EstimatedAmountMg = EstimatedLengthInM * (250 * pi() * filaments.MaterialDensityGramPerCubicCm * filaments.DiameterMm * filaments.DiameterMm)
FROM PrintFilament pf
INNER JOIN Filaments ON pf.FilamentId = Filaments.Id
WHERE 
(pf.EstimatedLengthInM is not null and pf.EstimatedAmountMg is NULL)
	AND DiameterMm > 0
	AND MaterialDensityGramPerCubicCm > 0
");

            migrationBuilder.Sql(@"
/* Convert Lengths to Amounts */
UPDATE pf
SET AmountMg = LengthInM * (250 * pi() * filaments.MaterialDensityGramPerCubicCm * filaments.DiameterMm * filaments.DiameterMm)
FROM PrintFilament pf
INNER JOIN Filaments ON pf.FilamentId = Filaments.Id
WHERE 
(pf.LengthInM is not null and pf.AmountMg is NULL)
	AND DiameterMm > 0
	AND MaterialDensityGramPerCubicCm > 0
");
        }
          /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
