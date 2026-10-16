using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <summary>
    /// Repairs the adjustments whose CreatedDate a spool save wiped to 0001-01-01 (#256).
    /// </summary>
    /// <inheritdoc />
    public partial class RepairFilamentAdjustmentCreatedDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data only, no schema change, so it is safe while the old app is still running. The
            // original date is gone; UpdatedDate (the last save that re-sent the row) is the
            // closest stored value and never earlier than the real one. Idempotent: a repaired
            // row no longer matches. An adjustment the old app re-saves after this runs and
            // before the new app is live gets wiped again, which only a re-run would fix.
            migrationBuilder.Sql(@"
UPDATE FilamentAdjustments
SET CreatedDate = UpdatedDate
WHERE CreatedDate < '2000-01-01'
  AND UpdatedDate >= '2000-01-01';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the wiped dates are not recoverable, and restoring 0001-01-01
            // would only reintroduce the bug's damage.
        }
    }
}
