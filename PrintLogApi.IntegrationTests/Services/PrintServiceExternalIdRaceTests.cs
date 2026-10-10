using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The race <see cref="PrintService.CreatePrint"/> has to survive (#144): two creates with one
/// external pair both find nothing, both insert, and IX_Prints_User_ExternalSource_ExternalId
/// rejects the second.
/// </summary>
/// <remarks>
/// Built deterministically rather than with concurrent requests: the suite shares one in-memory
/// SQLite connection, which faults ("nested transactions") when two requests save at once, so a
/// concurrent test would fail on the harness, not the code. Here an interceptor commits the
/// competing print from a second context in the window between the lookup and the insert.
/// </remarks>
public class PrintServiceExternalIdRaceTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task CreatePrint_LosesTheInsertRace_ReplaysTheWinner()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var shared = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var externalId = $"race:{Guid.NewGuid()}";

        var winner = new CommitCompetitorFirst(shared.Database.GetDbConnection(), externalId);
        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(shared.Database.GetDbConnection())
            .AddInterceptors(winner)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var racingContext = new PrintLogContext(options);
        var service = ActivatorUtilities.CreateInstance<PrintService>(scope.ServiceProvider, racingContext);

        var result = await service.CreatePrint(new AddPrintDTO
        {
            Title = "race",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Printing,
            ViewStatus = PrintViewStatus.Private,
            ExternalSource = "moonraker",
            ExternalId = externalId,
        }, IntegrationTestSeeder.TestUserId, PrintSource.ApiKey);

        Assert.True(winner.Fired, "the competing insert never ran, so no race was tested");
        Assert.True(result.WasReplayed);
        Assert.Equal(winner.WinnerId, result.Print.Id);
        Assert.Equal(1, await shared.Prints.CountAsync(p => p.ExternalId == externalId, ct));
    }

    /// <summary>
    /// Just before the first save that inserts the pair, commits a print with that pair from a
    /// separate context, as a concurrent request that got there first would.
    /// </summary>
    private sealed class CommitCompetitorFirst(System.Data.Common.DbConnection connection, string externalId)
        : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }
        public long WinnerId { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var inserting = eventData.Context!.ChangeTracker.Entries<Print>()
                .Any(e => e.State == EntityState.Added && e.Entity.ExternalId == externalId);
            if (inserting && !Fired)
            {
                Fired = true;
                var options = new DbContextOptionsBuilder<PrintLogContext>()
                    .UseSqlite(connection)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options;
                await using var other = new PrintLogContext(options);
                var print = new Print
                {
                    Title = "winner",
                    PrinterId = IntegrationTestSeeder.TestPrinterId,
                    Status = PrintStatus.Printing,
                    ViewStatus = PrintViewStatus.Private,
                    CreatedById = IntegrationTestSeeder.TestUserId,
                    UpdatedById = IntegrationTestSeeder.TestUserId,
                    ExternalSource = "moonraker",
                    ExternalId = externalId,
                };
                other.Prints.Add(print);
                await other.SaveChangesAsync(cancellationToken);
                WinnerId = print.Id;
            }

            return result;
        }
    }
}
