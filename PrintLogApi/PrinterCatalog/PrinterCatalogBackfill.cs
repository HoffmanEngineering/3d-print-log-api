using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Services;

namespace PrintLogApi.PrinterCatalog;

/// <summary>What one backfill pass did: printers linked, left ambiguous, and with no match.</summary>
public sealed record BackfillResult(int Linked, int Ambiguous, int Unmatched);

/// <summary>
/// Links existing printers to catalog models (spec §6.6): each printer without a
/// <see cref="Printer.CatalogModelId"/> is matched on "Make Model" and linked only on an exact or
/// alias match. A fuzzy match is left unlinked and counted as ambiguous. Nothing but that one
/// column is written, and the cached printer lists of the users it touched are invalidated.
/// </summary>
public sealed class PrinterCatalogBackfill(
    IServiceScopeFactory scopeFactory,
    IPrinterCatalogIndex index,
    ICacheVersionService cacheVersions,
    ILogger<PrinterCatalogBackfill> logger)
{
    private const int BatchSize = 500;

    /// <summary>
    /// Runs one pass over the unlinked printers, in batches by id, so the whole table is never in
    /// memory. Match only offers visible, non-retired models; the index is checked again before
    /// every link anyway. Needs a built index: before the first rebuild it does nothing.
    /// </summary>
    public async Task<BackfillResult> RunOnceAsync(CancellationToken ct)
    {
        if (!index.IsReady)
        {
            logger.LogWarning("The printer catalog index is not built yet; skipping the backfill");
            return new BackfillResult(0, 0, 0);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        int linked = 0, ambiguous = 0, unmatched = 0;
        var lastId = 0L;

        while (true)
        {
            var afterId = lastId;
            var batch = await db.Printers.AsNoTracking()
                .Where(p => p.CatalogModelId == null && p.Id > afterId)
                .OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.UserId, p.Make, p.Model })
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }
            lastId = batch[^1].Id;

            var links = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            var users = new HashSet<long>();
            foreach (var printer in batch)
            {
                var name = $"{printer.Make} {printer.Model}".Trim();
                var match = name.Length == 0 ? null : index.Match(name);
                if (match == null)
                {
                    unmatched++;
                }
                else if (match.Confidence == CatalogMatch.Fuzzy)
                {
                    ambiguous++;
                }
                else if (index.Get(match.Id) is not { Retired: false, Hidden: false })
                {
                    unmatched++;
                }
                else
                {
                    if (!links.TryGetValue(match.Id, out var ids))
                    {
                        links[match.Id] = ids = [];
                    }
                    ids.Add(printer.Id);
                    users.Add(printer.UserId);
                }
            }

            foreach (var (modelId, ids) in links)
            {
                // Still unlinked at write time: a printer the user linked meanwhile is left alone.
                linked += await db.Printers
                    .Where(p => ids.Contains(p.Id) && p.CatalogModelId == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.CatalogModelId, modelId), ct);
            }

            // The cached printer summaries carry catalogModelId.
            foreach (var userId in users)
            {
                cacheVersions.InvalidateUserCache(userId);
            }
        }

        return new BackfillResult(linked, ambiguous, unmatched);
    }
}
