using Microsoft.Data.Sqlite;

namespace PrintLogApi.IntegrationTests;

/// <summary>
/// Teaches SQLite the collation name the production search predicate uses.
///
/// Print search compares <c>LOWER(col) COLLATE Latin1_General_BIN2</c>. That is a SQL Server
/// collation, and SQLite rejects the statement outright with "no such collation sequence"
/// rather than ignoring it — so without this registration every test that runs a search throws.
///
/// The comparison registered here is ordinal, which is what BIN2 means, but it is very nearly
/// inert: SQLite translates .Contains() to <c>instr(...)</c>, and instr ignores collations
/// entirely. The name simply has to exist for the statement to parse. That is the whole point —
/// it lets the SAME query shape run on both providers with no provider branch in production code.
///
/// It does NOT make SQLite semantics match SQL Server. SQLite's LOWER() folds ASCII only, so
/// non-ASCII behaviour differs. Keep semantics tests on this host ASCII-only; the SQL Server
/// behaviour is covered by asserting generated SQL and by the differential corpus in
/// docs/superpowers/specs/2026-09-02-print-search-collation-benchmark.sql.
/// </summary>
public static class SqliteSearchCollation
{
    public static void Register(SqliteConnection connection) =>
        connection.CreateCollation(
            PrintLogApi.Services.PrintSearchPredicate.BinaryCollation,
            static (a, b) => string.CompareOrdinal(a, b));
}
