using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Models;
using PrintLogApi.Services.Sync;
using Xunit;

namespace PrintLogApi.IntegrationTests.Sync;

/// <summary>
/// The snapshot's cache miss must cost a fixed number of statements, however many printers and
/// spools the user has: a per-printer query for the current or last print, or a per-spool query for
/// its remaining figure, is the N+1 this endpoint exists to replace on the client side.
/// </summary>
public class SyncSnapshotQueryShapeTests(SyncTestFactory factory) : IClassFixture<SyncTestFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>A context over the factory's own SQLite connection, with a recorder attached.</summary>
    private (PrintLogContext Db, CommandRecorder Recorder, IServiceScope Scope) Recording()
    {
        var scope = factory.Services.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<PrintLogContext>().Database.GetDbConnection();
        var recorder = new CommandRecorder();
        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(connection)
            .AddInterceptors(recorder)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return (new PrintLogContext(options), recorder, scope);
    }

    private async Task<long> UserWithAsync(int printers)
    {
        var user = await factory.CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        for (var i = 0; i < printers; i++)
        {
            var printer = new Printer { Name = $"P{i}", UserId = user.Id, IsActive = true, CategoryNickname = "FDM", SlotCount = 2 };
            db.Printers.Add(printer);
            var spool = new Filament
            {
                Id = Guid.NewGuid(),
                DisplayName = $"S{i}",
                MaterialCategoryNickname = "filament",
                MaterialDensityGramPerCubicCm = 1.24,
                DiameterMm = 1.75,
                InitialNominalWeightMg = 1_000_000,
                SpoolNumber = i + 1,
                IsActive = true,
                CreatedById = user.Id,
                UpdatedById = user.Id,
            };
            db.Filaments.Add(spool);
            await db.SaveChangesAsync(Ct);
            db.PrinterFilament.Add(new PrinterFilament { PrinterId = printer.Id, FilamentId = spool.Id, Slot = 0, LoadedDateTime = SyncTestFactory.Start });
            foreach (var status in new[] { Print.PrintStatus.Printing, Print.PrintStatus.Success })
            {
                var print = new Print
                {
                    Title = $"{status}",
                    PrinterId = printer.Id,
                    Status = status,
                    StartDate = SyncTestFactory.Start.AddHours(-i - 1),
                    ViewStatus = Print.PrintViewStatus.Private,
                    CreatedById = user.Id,
                    UpdatedById = user.Id,
                };
                db.Prints.Add(print);
                await db.SaveChangesAsync(Ct);
                db.PrintFilament.Add(new PrintFilament { PrintId = print.Id, FilamentId = spool.Id, AmountMg = 1_000, Source = PrintFilament.SourceMeasurement.Weight });
            }

            await db.SaveChangesAsync(Ct);
        }

        return user.Id;
    }

    private async Task<int> MissStatementsAsync(long userId)
    {
        var (db, recorder, scope) = Recording();
        using (scope)
        await using (db)
        {
            var month = SyncMonth.Containing(SyncTestFactory.Start, TimeZoneInfo.Utc, "UTC");
            var snapshot = await SyncSnapshotService.LoadAsync(db, userId, "v", month, Ct);
            Assert.NotEmpty(snapshot.Printers);
            Assert.All(snapshot.Printers, p => Assert.NotNull(p.CurrentPrint));
            Assert.All(snapshot.Printers, p => Assert.NotNull(p.LastFinishedPrint));
            Assert.All(snapshot.Printers, p => Assert.Single(p.Loaded));
            return recorder.Commands.Count;
        }
    }

    [Fact]
    public async Task CacheMiss_StatementCount_DoesNotGrowWithThePrinters()
    {
        var one = await MissStatementsAsync(await UserWithAsync(1));
        var four = await MissStatementsAsync(await UserWithAsync(4));

        Assert.Equal(5, one);
        Assert.Equal(one, four);
    }

    [Fact]
    public async Task ConnectionOverlay_IsOneStatement()
    {
        var userId = await UserWithAsync(3);
        var (db, recorder, scope) = Recording();
        using (scope)
        await using (db)
        {
            await SyncSnapshotService.LoadConnectionsAsync(db, userId, SyncTestFactory.Start, Ct);
            Assert.Single(recorder.Commands);
        }
    }
}
