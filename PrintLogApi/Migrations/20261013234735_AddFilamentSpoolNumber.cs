using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <inheritdoc />
    public partial class AddFilamentSpoolNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastSpoolNumber",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SpoolNumber",
                table: "Filaments",
                type: "int",
                nullable: true);

            // Backfill (#174): each user's existing spools numbered 1..n by CreatedDate, then Id,
            // and their high-water mark set to n. Between the column adds above and the unique
            // index below, so the index is built over finished data.
            //
            // EXEC, because both statements name a column this migration adds. The deploy
            // workflow's reviewed artifact is an --idempotent script, which wraps each operation
            // in IF NOT EXISTS (...) BEGIN ... END; SQL Server compiles that whole batch before
            // running it, and a bare reference to SpoolNumber fails as an invalid column name
            // on any database where it does not exist yet. EXEC defers compilation to run time.
            //
            // Additive and old-app safe: the previous version never reads either column. A spool
            // it creates after this statement runs stays unnumbered, and the new app numbers it
            // on first lookup (FilamentService.NumberUnnumberedSpoolsAsync).
            migrationBuilder.Sql(@"
EXEC(N'
WITH numbered AS (
    SELECT SpoolNumber,
           ROW_NUMBER() OVER (PARTITION BY CreatedById ORDER BY CreatedDate, Id) AS n
    FROM Filaments
)
UPDATE numbered SET SpoolNumber = n;

UPDATE u SET LastSpoolNumber = m.MaxNumber
FROM Users u
INNER JOIN (SELECT CreatedById, MAX(SpoolNumber) AS MaxNumber
            FROM Filaments
            GROUP BY CreatedById) m ON m.CreatedById = u.Id;
')");

            migrationBuilder.CreateIndex(
                name: "IX_Filaments_CreatedById_SpoolNumber",
                table: "Filaments",
                columns: new[] { "CreatedById", "SpoolNumber" },
                unique: true,
                filter: "[SpoolNumber] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Filaments_CreatedById_SpoolNumber",
                table: "Filaments");

            migrationBuilder.DropColumn(
                name: "LastSpoolNumber",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SpoolNumber",
                table: "Filaments");
        }
    }
}
