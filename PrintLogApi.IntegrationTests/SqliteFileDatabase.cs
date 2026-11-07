using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PrintLogApi.Achievements.Triggers;

namespace PrintLogApi.IntegrationTests;

/// <summary>
/// A SQLite database in a temporary file, for a host whose code uses the database from more than
/// one thread at once: a background worker running beside requests, or a test that fires requests
/// in parallel.
///
/// <para>The suite's usual database is one in-memory connection shared by every
/// <see cref="PrintLogContext"/>, and that only works while one thing uses it at a time. EF
/// registers its SQL functions on the connection again for every new context, and SQLite refuses
/// to change a function while any statement on that connection is running, so a context created
/// while another thread's query is mid-read fails with SQLite error 5 ("database is locked" or
/// "unable to delete/modify user-function due to active statements"). Here every context opens its
/// own connection to the file instead, as every request has its own connection on SQL Server.</para>
///
/// <para>Call <see cref="UseWriteAheadLogging"/> once the schema is in place. In SQLite's default
/// rollback journal a write cannot commit while another connection is reading, and EF's inserts
/// (<c>INSERT ... RETURNING</c>) commit when Microsoft.Data.Sqlite resets the statement, where it
/// does not apply its busy retry: the insert fails at once with "database is locked", whatever
/// <c>Default Timeout</c> says. In WAL mode readers never block a writer, and a writer waiting for
/// another writer waits at the step, which is retried.</para>
/// </summary>
public sealed class SqliteFileDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"printlog-tests-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_path};Default Timeout=30";

    /// <summary>Replaces the host's <see cref="PrintLogContext"/> registration with one on this file.</summary>
    public void Register(IServiceCollection services, params IInterceptor[] interceptors)
    {
        services.RemoveAll<DbContextOptions<PrintLogContext>>();
        services.AddDbContext<PrintLogContext>((sp, options) =>
        {
            options.UseSqlite(ConnectionString);
            // Same hooks as the shared-connection host.
            options.AddAchievementInterceptors(sp);
            options.AddInterceptors(SearchCollationOnOpen.Instance);
            options.AddInterceptors(interceptors);
            options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        });
    }

    /// <summary>
    /// Switches the file to write-ahead logging. Call it after the schema is copied in: the copy is
    /// a backup from an in-memory template, which writes the template's (rollback) journal mode
    /// into the file header.
    /// </summary>
    public static void UseWriteAheadLogging(PrintLogContext db)
    {
        var connection = db.Database.GetDbConnection();
        db.Database.OpenConnection();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = command.ExecuteScalar() as string;
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"SQLite kept journal mode '{mode}' instead of WAL.");
            }
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    public void Dispose()
    {
        using (var connection = new SqliteConnection(ConnectionString))
        {
            SqliteConnection.ClearPool(connection);
        }
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    /// <summary>Registers the search collation on every connection, as the shared-connection host does once.</summary>
    private sealed class SearchCollationOnOpen : DbConnectionInterceptor
    {
        public static readonly SearchCollationOnOpen Instance = new();

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
            SqliteSearchCollation.Register((SqliteConnection)connection);

        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            SqliteSearchCollation.Register((SqliteConnection)connection);
            return Task.CompletedTask;
        }
    }
}
