using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class BackfillFilamentColors : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1: Populate Colors from ColorHex as a single-element JSON array
            migrationBuilder.Sql(@"
                UPDATE Filaments
                SET Colors = '[\""' + ColorHex + '\""]'
                WHERE Colors IS NULL
                  AND ColorHex IS NOT NULL
                  AND ColorHex != ''
            ");

            // Step 2: Default ColorPattern to Solid for all newly-populated rows
            migrationBuilder.Sql(@"
                UPDATE Filaments
                SET ColorPattern = 1
                WHERE ColorPattern IS NULL
                  AND Colors IS NOT NULL
            ");

            // Step 3: Clear ColorPattern back to NULL for obvious multi-color filaments —
            // we only have one hex value for these so Solid would be actively wrong,
            // and NULL signals to users that the field still needs to be filled in.
            migrationBuilder.Sql(@"
                UPDATE Filaments
                SET ColorPattern = NULL
                WHERE Colors IS NOT NULL
                  AND (   LOWER(ColorName)   LIKE '%rainbow%'
                       OR LOWER(ColorName)   LIKE '%gradient%'
                       OR LOWER(ColorName)   LIKE '%ombre%'
                       OR LOWER(ColorName)   LIKE '%multicolor%'
                       OR LOWER(ColorName)   LIKE '%multi color%'
                       OR LOWER(ColorName)   LIKE '%dual%'
                       OR LOWER(DisplayName) LIKE '%rainbow%'
                       OR LOWER(DisplayName) LIKE '%gradient%')
            ");

            // Step 4: Default FinishType to Standard for all newly-populated rows
            migrationBuilder.Sql(@"
                UPDATE Filaments
                SET FinishType = 1
                WHERE FinishType IS NULL
                  AND Colors IS NOT NULL
            ");

            // Step 5: Patch Silk finish — keyword appears in both ColorName and DisplayName
            migrationBuilder.Sql(@"
                UPDATE Filaments
                SET FinishType = 2
                WHERE Colors IS NOT NULL
                  AND (   LOWER(ColorName)   LIKE '%silk%'
                       OR LOWER(DisplayName) LIKE '%silk%')
            ");

            // Step 6: Patch Matte finish — no overlap with Silk (verified: 0 rows match both)
            migrationBuilder.Sql(@"
                UPDATE Filaments
                SET FinishType = 3
                WHERE Colors IS NOT NULL
                  AND (   LOWER(ColorName)   LIKE '%matte%'
                       OR LOWER(DisplayName) LIKE '%matte%')
            ");

            // Step 7: Infer Effects from ColorName/DisplayName keywords.
            // Priority order (GlowInDark first, Translucent last) handles the two rows
            // that match both glow and sparkle signals — they get GlowInDark.
            // MetalFill is intentionally skipped: "Metallic" describes a silk-like finish
            // as often as it describes actual metal-filled filament.
            migrationBuilder.Sql(@"
                UPDATE Filaments
                SET Effects = CASE
                    WHEN LOWER(ColorName) LIKE '%glow%'
                      OR LOWER(ColorName) LIKE '%gitd%'
                      OR LOWER(ColorName) LIKE '%luminous%'
                      OR LOWER(ColorName) LIKE '%noctilucent%'
                      OR LOWER(ColorName) LIKE '%fosforescente%'
                    THEN '[2]'
                    WHEN LOWER(ColorName)   LIKE '%wood%'
                      OR LOWER(ColorName)   LIKE '%walnut%'
                      OR LOWER(ColorName)   LIKE '%cedar%'
                      OR LOWER(ColorName)   LIKE '%birch%'
                      OR LOWER(ColorName)   LIKE '%teak%'
                      OR LOWER(ColorName)   LIKE '%ebony%'
                      OR LOWER(DisplayName) LIKE '%wood%'
                    THEN '[5]'
                    WHEN LOWER(ColorName)   LIKE '%carbon fiber%'
                      OR LOWER(ColorName)   LIKE '%carbon fibre%'
                      OR LOWER(DisplayName) LIKE '%carbon fiber%'
                      OR LOWER(DisplayName) LIKE '%carbon fibre%'
                      OR LOWER(DisplayName) LIKE '%carbonx%'
                    THEN '[4]'
                    WHEN LOWER(ColorName) LIKE '%galaxy%'
                      OR LOWER(ColorName) LIKE '%sparkle%'
                      OR LOWER(ColorName) LIKE '%glitter%'
                    THEN '[1]'
                    WHEN LOWER(ColorName) LIKE '%fluorescent%'
                      OR LOWER(ColorName) LIKE '%fluor%'
                      OR LOWER(ColorName) LIKE '%neon%'
                    THEN '[7]'
                    WHEN LOWER(ColorName) LIKE '%translucent%'
                      OR LOWER(ColorName) LIKE '%transparent%'
                    THEN '[3]'
                    ELSE NULL
                END
                WHERE Colors IS NOT NULL
                  AND Effects IS NULL
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE Filaments
                SET Colors = NULL, ColorPattern = NULL, FinishType = NULL, Effects = NULL
                WHERE Colors IS NOT NULL
            ");
        }
    }
}
