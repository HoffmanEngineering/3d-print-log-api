using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;

namespace PrintLogApi.SpoolTags.Matching;

internal sealed record SkuTally(string EntryId, int Votes, int Users);

public sealed record CatalogMatch(string Id, double Confidence, int? SpoolWeightG, string? ColorName, string Name, string Manufacturer);

/// <summary>Finds the catalog entry a tag reading describes: a trusted SKU first, then a scored match.</summary>
public sealed class FilamentCatalogMatcher(PrintLogContext db)
{
    public const double MatchThreshold = 0.7;
    private const double DiameterToleranceMm = 0.01;

    public async Task<CatalogMatch?> MatchAsync(TagReading reading, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(reading.Format) && !string.IsNullOrEmpty(reading.Sku))
        {
            var trustedId = await TrustedEntryIdAsync(reading.Format, reading.Sku, ct);
            if (trustedId != null)
            {
                var trusted = await db.FilamentCatalogEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == trustedId, ct);
                if (trusted != null)
                {
                    return ToMatch(trusted, 1.0);
                }
            }
        }

        var manufacturer = (reading.Manufacturer ?? reading.Brand)?.Trim();
        var materialKey = MaterialKey.Normalize(reading.MaterialType);
        if (string.IsNullOrEmpty(manufacturer) || materialKey.Length == 0)
        {
            return null;
        }

        // ToUpper() equality translates on SQLite (binary compare) and SQL Server (CI_AS) alike.
        // That is ASCII-only on SQLite, which is acceptable for vendor names.
        // Material and diameter are checked in memory on the narrowed set.
        var upper = manufacturer.ToUpperInvariant();
        var candidates = await CandidatesQuery(db, upper).ToListAsync(ct);

        return candidates
            .Where(e => MaterialKey.Normalize(e.Material) == materialKey)
            .Where(e => reading.DiameterMm is not { } d || Math.Abs(e.DiameterMm - d) <= DiameterToleranceMm)
            .Select(e => (Entry: e, Score: Score(reading, e)))
            .Where(x => x.Score >= MatchThreshold)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Id, StringComparer.Ordinal)
            .Select(x => ToMatch(x.Entry, x.Score))
            .FirstOrDefault();
    }

    /// <summary>
    /// The entry most users agree a (format, sku) is: at least 2 distinct users and strictly more
    /// votes than the runner-up. Null when nothing qualifies or the winner was removed from the catalog.
    /// </summary>
    public async Task<string?> TrustedEntryIdAsync(string format, string sku, CancellationToken ct)
    {
        // Format and SKU compare binary on SQLite and CI_AS on SQL Server; acceptable for numeric SKUs.
        var tallies = await TallyQuery(db, format, sku).ToListAsync(ct);

        var ranked = tallies.OrderByDescending(t => t.Votes).ToList();
        if (ranked.Count == 0)
        {
            return null;
        }
        var top = ranked[0];
        if (top.Users < 2 || (ranked.Count > 1 && ranked[1].Votes >= top.Votes))
        {
            return null;
        }

        var live = await db.FilamentCatalogEntries.AsNoTracking()
            .AnyAsync(e => e.Id == top.EntryId && e.RemovedAt == null, ct);
        return live ? top.EntryId : null;
    }

    // Kept as named queries so a test can compile them through the SQL Server provider.
    internal static IQueryable<FilamentCatalogEntry> CandidatesQuery(PrintLogContext db, string upperManufacturer) =>
        db.FilamentCatalogEntries.AsNoTracking()
            .Where(e => e.RemovedAt == null && e.Manufacturer.ToUpper() == upperManufacturer);

    internal static IQueryable<SkuTally> TallyQuery(PrintLogContext db, string format, string sku) =>
        db.FilamentCatalogSkus.AsNoTracking()
            .Where(s => s.Format == format && s.Sku == sku)
            .GroupBy(s => s.CatalogEntryId)
            .Select(g => new SkuTally(g.Key, g.Count(), g.Select(s => s.UserId).Distinct().Count()));

    private static double Score(TagReading reading, FilamentCatalogEntry entry)
    {
        var score = 0.5;
        var distance = ColorDistance.Rgb(reading.Colors.FirstOrDefault(), entry.ColorHex);
        score += 0.3 * ColorDistance.Closeness(distance);
        if (reading.NetWeightG == entry.WeightG)
        {
            score += 0.1;
        }
        if (!string.IsNullOrWhiteSpace(reading.MaterialSubtype)
            && entry.Name.Contains(reading.MaterialSubtype, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.1;
        }
        return score;
    }

    private static CatalogMatch ToMatch(FilamentCatalogEntry e, double confidence) =>
        new(e.Id, confidence, e.SpoolWeightG, e.Name, e.Name, e.Manufacturer);
}
