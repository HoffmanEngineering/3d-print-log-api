using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace PrintLogApi.Extensions;

/// <summary>
/// Recognizes a unique-index violation on both providers this codebase runs on, so recovery paths
/// written against SQL Server are exercised by the SQLite integration suite too.
/// </summary>
public static class UniqueConstraintViolation
{
    public static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException switch
    {
        SqlException sql => sql.Number is 2601 or 2627,
        // SQLITE_CONSTRAINT_UNIQUE (2067) and SQLITE_CONSTRAINT_PRIMARYKEY (1555).
        SqliteException sqlite => sqlite.SqliteExtendedErrorCode is 2067 or 1555,
        _ => false,
    };
}
