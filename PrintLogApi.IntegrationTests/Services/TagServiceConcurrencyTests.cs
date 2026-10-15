using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.Models.User;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// SetPrintTagsAsync against the interleavings a single request cannot produce on its own: a
/// stale tracked print, and another request inserting the same link first.
/// </summary>
public class TagServiceConcurrencyTests
{
    private sealed record World(SqliteConnection Connection, long UserId, long PrintId, Guid TagId);

    private static async Task<World> Seed(CancellationToken ct)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        SqliteSearchCollation.Register(connection);

        await using var context = NewContext(connection);
        await context.Database.EnsureCreatedAsync(ct);

        var user = new User { OAuthUserId = $"auth0|tags-{Guid.NewGuid()}", ViewStatus = ProfileViewStatus.Public };
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);
        var printer = new Printer { Name = "Printer", UserId = user.Id, IsActive = true };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(ct);
        var print = new Print
        {
            Title = "Before",
            Status = Print.PrintStatus.Printing,
            ViewStatus = Print.PrintViewStatus.Private,
            PrinterId = printer.Id,
            CreatedById = user.Id,
            UpdatedById = user.Id,
        };
        var tag = new Tag { Id = Guid.NewGuid(), Name = "Gifts", NormalizedName = "GIFTS", CreatedById = user.Id, UpdatedById = user.Id };
        context.Prints.Add(print);
        context.Tags.Add(tag);
        await context.SaveChangesAsync(ct);

        return new World(connection, user.Id, print.Id, tag.Id);
    }

    private static PrintLogContext NewContext(SqliteConnection connection, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptors)
            .Options);

    private static PrintLogContext RetryingContext(SqliteConnection connection, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(connection, o => o.ExecutionStrategy(d => new RetryOnceStrategy(d)))
            .AddInterceptors(interceptors)
            .Options);

    private static TagService Service(PrintLogContext context) =>
        new(context, new CacheVersionService(new MemoryCache(new MemoryCacheOptions())));

    [Fact]
    public async Task SetPrintTags_DoesNotOverwriteAFieldAnotherRequestChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await Seed(ct);
        await using var _ = world.Connection;

        // This context holds a snapshot of the print from before /complete ran.
        await using var stale = NewContext(world.Connection);
        await stale.Prints.SingleAsync(p => p.Id == world.PrintId, ct);

        await using (var other = NewContext(world.Connection))
        {
            await other.Prints.Where(p => p.Id == world.PrintId).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Title, "After")
                .SetProperty(p => p.Status, Print.PrintStatus.Success), ct);
        }

        await Service(stale).SetPrintTagsAsync(world.UserId, world.PrintId, [world.TagId], ct);

        await using var check = NewContext(world.Connection);
        var print = await check.Prints.AsNoTracking().SingleAsync(p => p.Id == world.PrintId, ct);
        Assert.Equal("After", print.Title);
        Assert.Equal(Print.PrintStatus.Success, print.Status);
        Assert.True(await check.PrintTags.AnyAsync(pt => pt.PrintId == world.PrintId && pt.TagId == world.TagId, ct));
    }

    [Fact]
    public async Task SetPrintTags_LosingTheRaceToTheSameLink_IsAConflict_NotAServerError()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await Seed(ct);
        await using var _ = world.Connection;

        // Another request inserts the same link between this one's read and its insert.
        await using var context = NewContext(world.Connection,
            new InsertLinkFirst(world.PrintId, world.TagId));

        await Assert.ThrowsAsync<PrintTagsConflictException>(() =>
            Service(context).SetPrintTagsAsync(world.UserId, world.PrintId, [world.TagId], ct));
    }

    [Fact]
    public async Task SetPrintTags_SucceedsWhenTheStrategyRetriesAfterATransientFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await Seed(ct);
        await using var _ = world.Connection;

        // The first attempt adds the link to the change tracker and then fails, as a transient
        // SQL Server error would. The retry must not trip over that attempt's tracked entity.
        var failOnce = new FailFirstSave();
        await using var context = RetryingContext(world.Connection, failOnce);

        var tags = await Service(context).SetPrintTagsAsync(world.UserId, world.PrintId, [world.TagId], ct);

        Assert.Equal(2, failOnce.Attempts);
        Assert.Equal([world.TagId], tags!.Select(t => t.Id));
        await using var check = NewContext(world.Connection);
        Assert.Equal(1, await check.PrintTags.CountAsync(pt => pt.PrintId == world.PrintId, ct));
    }

    private sealed class TransientTestException() : Exception("transient");

    /// <summary>Retries once on <see cref="TransientTestException"/>, with no delay.</summary>
    private sealed class RetryOnceStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestException;

        protected override TimeSpan? GetNextDelay(Exception lastException) =>
            base.GetNextDelay(lastException) is null ? null : TimeSpan.Zero;
    }

    /// <summary>Fails the first SaveChanges after the entities are tracked, before any SQL runs.</summary>
    private sealed class FailFirstSave : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts == 1)
                throw new TransientTestException();
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Inserts the link itself just before EF's own INSERT into PrintTags runs.</summary>
    private sealed class InsertLinkFirst(long printId, Guid tagId) : DbCommandInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await InsertFirst(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await InsertFirst(command, cancellationToken);
            return result;
        }

        private async Task InsertFirst(DbCommand command, CancellationToken ct)
        {
            if (_done || !command.CommandText.Contains("INSERT INTO \"PrintTags\"", StringComparison.Ordinal))
                return;
            _done = true;
            await using var insert = command.Connection!.CreateCommand();
            insert.Transaction = command.Transaction;
            insert.CommandText = "INSERT INTO \"PrintTags\" (\"PrintId\", \"TagId\") VALUES ($p, $t)";
            insert.Parameters.Add(new SqliteParameter("$p", printId));
            insert.Parameters.Add(new SqliteParameter("$t", tagId.ToString().ToUpperInvariant()));
            await insert.ExecuteNonQueryAsync(ct);
        }
    }
}
