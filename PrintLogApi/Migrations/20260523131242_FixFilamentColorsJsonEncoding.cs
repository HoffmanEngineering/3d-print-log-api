using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class FixFilamentColorsJsonEncoding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The BackfillFilamentColors migration stored Colors as '[\"#RRGGBB\"]' (with literal
            // backslashes) instead of valid JSON '["#RRGGBB"]', due to a C# verbatim-string escaping
            // mistake. This strips the spurious backslashes from any affected rows.
            // CHAR(92) = backslash, CHAR(34) = double-quote. Avoids embedding quote
            // characters in a C# verbatim string and repeating the original escaping mistake.
            migrationBuilder.Sql(@"
                UPDATE Filaments
                SET Colors = REPLACE(Colors, CHAR(92) + CHAR(34), CHAR(34))
                WHERE Colors LIKE '%' + CHAR(92) + CHAR(34) + '%'
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — no value in reverting a data repair.
        }
    }
}
