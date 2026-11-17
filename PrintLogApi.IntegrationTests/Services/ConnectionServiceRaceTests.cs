using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Connection;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The two things that can change between <see cref="ConnectionService.Upsert"/>'s checks and its
/// save: a concurrent first heartbeat inserts the same instance, or the printer is deleted (#201).
/// </summary>
/// <remarks>
/// Built deterministically, as in <see cref="PrintServiceExternalIdRaceTests"/>: an interceptor
/// commits the competing write from a second context just before the first save.
/// </remarks>
public class ConnectionServiceRaceTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task Upsert_PrinterDeletedBeforeTheSave_IsUserCannotAccessPrinter()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var shared = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var printer = new Printer
        {
            Name = "Deleted mid-request",
            UserId = IntegrationTestSeeder.TestUserId,
            IsActive = true,
            CategoryNickname = "FDM",
        };
        shared.Printers.Add(printer);
        await shared.SaveChangesAsync(ct);
        var instanceId = $"race:{Guid.NewGuid()}";

        var competitor = new BeforeFirstSave(shared.Database.GetDbConnection(),
            other => other.Printers.Where(p => p.Id == printer.Id).ExecuteDeleteAsync(ct));
        var service = RacingService(scope, shared, competitor, out var racingContext);
        await using var _ = racingContext;

        await Assert.ThrowsAsync<UserCannotAccessPrinterException>(
            () => service.Upsert(IntegrationTestSeeder.TestUserId, instanceId, Body(printer.Id)));

        Assert.True(competitor.Fired, "the printer was never deleted, so no race was tested");
        Assert.False(await shared.Connections.AnyAsync(c => c.InstanceId == instanceId, ct));
    }

    [Fact]
    public async Task Upsert_LosesTheInsertRace_UpdatesTheWinner()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var shared = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var instanceId = $"race:{Guid.NewGuid()}";
        var winnerId = Guid.NewGuid();

        var competitor = new BeforeFirstSave(shared.Database.GetDbConnection(), other =>
        {
            other.Connections.Add(new Connection
            {
                Id = winnerId,
                UserId = IntegrationTestSeeder.TestUserId,
                InstanceId = instanceId,
                Kind = "moonraker",
                DisplayName = "winner",
                CreatedDate = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
            });
            return other.SaveChangesAsync(ct);
        });
        var service = RacingService(scope, shared, competitor, out var racingContext);
        await using var _ = racingContext;

        var (connection, created) = await service.Upsert(
            IntegrationTestSeeder.TestUserId, instanceId, Body(IntegrationTestSeeder.TestPrinterId));

        Assert.True(competitor.Fired, "the competing insert never ran, so no race was tested");
        Assert.False(created);
        Assert.Equal(winnerId, connection.Id);
        var stored = await shared.Connections.AsNoTracking().SingleAsync(c => c.InstanceId == instanceId, ct);
        Assert.Equal("Racing bridge", stored.DisplayName);
        Assert.Equal(IntegrationTestSeeder.TestPrinterId, stored.PrinterId);
    }

    private static ConnectionService RacingService(
        IServiceScope scope, PrintLogContext shared, BeforeFirstSave competitor, out PrintLogContext racingContext)
    {
        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(shared.Database.GetDbConnection())
            .AddInterceptors(competitor)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        racingContext = new PrintLogContext(options);
        return ActivatorUtilities.CreateInstance<ConnectionService>(scope.ServiceProvider, racingContext);
    }

    private static PutConnectionDto Body(long printerId) => new()
    {
        Kind = "moonraker",
        DisplayName = "Racing bridge",
        PrinterId = printerId,
    };

    /// <summary>
    /// Just before the first save, runs <paramref name="write"/> against a separate context, as a
    /// concurrent request that got there first would.
    /// </summary>
    private sealed class BeforeFirstSave(
        System.Data.Common.DbConnection connection, Func<PrintLogContext, Task> write)
        : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                var options = new DbContextOptionsBuilder<PrintLogContext>()
                    .UseSqlite(connection)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options;
                await using var other = new PrintLogContext(options);
                await write(other);
            }

            return result;
        }
    }
}
