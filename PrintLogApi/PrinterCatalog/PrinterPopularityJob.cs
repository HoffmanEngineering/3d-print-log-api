using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Models;

namespace PrintLogApi.PrinterCatalog;

/// <summary>
/// Once a day: counts, per catalog model, the distinct users with an active printer of that model
/// that was added or printed on within the popularity window (spec §6.5), writes the counts to
/// <see cref="PrinterModel.OwnerCount"/>, and rebuilds the index. The first tick waits five minutes
/// so a cold start is not slowed. A failed tick is logged and the loop carries on.
/// </summary>
public sealed class PrinterPopularityJob(
    IServiceScopeFactory scopeFactory,
    IPrinterCatalogIndex index,
    IOptions<PrinterCatalogOptions> options,
    TimeProvider timeProvider,
    ILogger<PrinterPopularityJob> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(Interval, timeProvider);
            do
            {
                try
                {
                    await RunOnceAsync(timeProvider.GetUtcNow(), stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Printer popularity job failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// The owner counts as of <paramref name="now"/>: one grouped query in SQL (prints are never
    /// loaded), then one <c>ExecuteUpdate</c> per model whose count changed, so a model that no
    /// longer qualifies drops to 0. Rebuilds the index once, after the writes. Writes only
    /// <see cref="PrinterModel.OwnerCount"/>: not <see cref="PrinterModel.UpdatedAt"/>, no fact.
    /// Returns the number of models updated.
    /// </summary>
    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var counts = await OwnerCounts(db, now.AddDays(-options.Value.PopularityWindowDays))
            .ToDictionaryAsync(x => x.CatalogModelId, x => x.Owners, StringComparer.Ordinal, ct);
        var current = await db.PrinterModels.AsNoTracking()
            .Select(m => new { m.Id, m.OwnerCount })
            .ToListAsync(ct);

        var updated = 0;
        foreach (var model in current)
        {
            var owners = counts.GetValueOrDefault(model.Id);
            if (owners == model.OwnerCount)
            {
                continue;
            }
            var id = model.Id;
            updated += await db.PrinterModels
                .Where(m => m.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.OwnerCount, owners), ct);
        }

        // Read after the writes committed, and rebuilt once per run. Never from an empty table into
        // an index that is not built yet: a failed first seed leaves it not ready (503) on purpose.
        var models = await db.PrinterModels.AsNoTracking().ToListAsync(ct);
        if (index.IsReady || models.Any(m => !m.Hidden && !m.Retired))
        {
            index.Rebuild(models);
        }
        logger.LogInformation("Printer popularity: {Updated} models updated", updated);
        return updated;
    }

    /// <summary>
    /// Distinct users per model over active printers added since <paramref name="cutoff"/> or with a
    /// print started since then. Public for the SQL Server translation test.
    /// </summary>
    internal static IQueryable<ModelOwners> OwnerCounts(PrintLogContext db, DateTimeOffset cutoff) =>
        db.Printers.AsNoTracking()
            .Where(p => p.IsActive && p.CatalogModelId != null
                && (p.CreatedAt >= cutoff || db.Prints.Any(pr => pr.PrinterId == p.Id && pr.StartDate >= cutoff)))
            .GroupBy(p => p.CatalogModelId!)
            .Select(g => new ModelOwners(g.Key, g.Select(p => p.UserId).Distinct().Count()));

    internal sealed record ModelOwners(string CatalogModelId, int Owners);
}
