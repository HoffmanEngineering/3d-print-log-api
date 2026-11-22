using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintLogApi.Migrations
{
    /// <summary>
    /// Indexes for the most expensive queries in production's Query Store (Nov 2026):
    /// <list type="bullet">
    /// <item><c>IX_Prints_PrinterId_StartDate</c>: <c>PrinterPopularityJob</c>'s per-printer
    /// "printed since the cutoff" check timed out (30 s) on every daily run, and
    /// <c>PrinterService.LinkPrintingRows</c> took up to 30 s. It replaces the foreign key's
    /// <c>IX_Prints_PrinterId</c>, which it leads with.</item>
    /// <item><c>IX_Prints_Id_Covering</c>: the filament lists' remaining weight looked up each usage
    /// row's print in the clustered index, thousands of wide-row reads a call.</item>
    /// <item>Drops <c>nci_wi_Prints_CE6B909CB940069FDAF02ECD05BA73A9</c>, which Azure automatic
    /// tuning created outside the model: about 940 MB (a third copy of <c>Notes</c>) that served
    /// one seek in two weeks, and was written on every print save.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Hand-written in the shape of <see cref="ReduceDatabaseLoadIndexes"/>: each statement outside
    /// the migration's transaction, guarded by <c>IF [NOT] EXISTS</c> so a part-way failure can run
    /// again, and built <c>ONLINE</c> with an offline retry on error 1712 (Express). The new
    /// PrinterId index is built before the one it replaces is dropped.
    /// </remarks>
    public partial class QueryStoreIndexes : Migration
    {
        private const string AutoTunedNotesIndex = "nci_wi_Prints_CE6B909CB940069FDAF02ECD05BA73A9";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CreateOnline(migrationBuilder, "Prints", "IX_Prints_PrinterId_StartDate",
                "([PrinterId], [StartDate]) INCLUDE ([Status])");

            CreateOnline(migrationBuilder, "Prints", "IX_Prints_Id_Covering",
                "([Id]) INCLUDE ([CreatedById], [PrinterId], [StartDate], [CreatedDate])");

            DropIfExists(migrationBuilder, "Prints", "IX_Prints_PrinterId");

            // Exists only where automatic tuning created it (production); nothing refers to it.
            DropIfExists(migrationBuilder, "Prints", AutoTunedNotesIndex);
        }

        private static void CreateOnline(MigrationBuilder migrationBuilder, string table, string name, string definition)
        {
            var create = $"CREATE INDEX [{name}] ON [{table}] {definition}";
            migrationBuilder.Sql($@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'[{table}]'))
BEGIN
    BEGIN TRY
        {create} WITH (ONLINE = ON);
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 1712 THROW;
        {create};
    END CATCH
END
", suppressTransaction: true);
        }

        private static void DropIfExists(MigrationBuilder migrationBuilder, string table, string name) =>
            migrationBuilder.Sql($@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'[{table}]'))
    DROP INDEX [{name}] ON [{table}];
", suppressTransaction: true);

        /// <inheritdoc />
        /// <remarks>
        /// The auto-tuned index is not recreated: it was never part of the model.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            CreateOnline(migrationBuilder, "Prints", "IX_Prints_PrinterId", "([PrinterId])");
            DropIfExists(migrationBuilder, "Prints", "IX_Prints_Id_Covering");
            DropIfExists(migrationBuilder, "Prints", "IX_Prints_PrinterId_StartDate");
        }
    }
}
