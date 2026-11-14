using Microsoft.EntityFrameworkCore;
using PrintLogApi.SpoolTags.Labels;

namespace PrintLogApi.SpoolTags.Matching;

internal sealed record RankCandidate(
    Guid Id,
    string? Brand,
    string? MaterialType,
    List<string>? Colors,
    string? ColorHex,
    long? InitialNominalWeightMg,
    bool HasTags,
    DateTime UpdatedDate,
    string? ColorName,
    string? DisplayName);

public sealed record TagSuggestion(Guid FilamentId, double Score, IReadOnlyList<string> Reasons);

/// <summary>Scores a user's own materials against a tag reading, for a tag that is not linked yet.</summary>
public sealed class TagSuggestionRanker(PrintLogContext db)
{
    public const int MaxSuggestions = 5;
    public const double MinScore = 0.5;

    public async Task<IReadOnlyList<TagSuggestion>> RankAsync(long userId, TagReading? reading, CancellationToken ct)
    {
        var materialKey = MaterialKey.Normalize(reading?.MaterialType);
        if (reading == null || materialKey.Length == 0)
        {
            return [];
        }

        // Colors goes through a value conversion, so it is projected whole and read in memory.
        var candidates = await CandidatesQuery(db, userId).ToListAsync(ct);

        var brands = new[] { reading.Brand, reading.Manufacturer }
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b!.Trim())
            .ToList();

        return candidates
            .Where(f => SameMaterial(materialKey, MaterialKey.Normalize(f.MaterialType)))
            .Select(f =>
            {
                var score = 0.4;
                var reasons = new List<string> { "same-material" };

                var color = f.Colors is { Count: > 0 } ? f.Colors[0] : f.ColorHex;
                var closeness = ColorDistance.Closeness(ColorDistance.Rgb(reading.Colors.FirstOrDefault(), color));
                if (closeness >= 0.5)
                {
                    score += 0.3 * closeness;
                    reasons.Add("close-color");
                }
                if (f.Brand != null && brands.Any(b => string.Equals(b, f.Brand.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    score += 0.15;
                    reasons.Add("same-brand");
                }
                if (reading.NetWeightG is { } grams && f.InitialNominalWeightMg is { } mg
                    && (long)Math.Round(mg / 1000.0) == grams)
                {
                    score += 0.05;
                    reasons.Add("same-weight");
                }
                if (!f.HasTags)
                {
                    score += 0.1;
                    reasons.Add("untagged");
                }
                return (f.Id, f.UpdatedDate, Score: Math.Round(score, 4), Reasons: reasons);
            })
            .Where(x => x.Score >= MinScore)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.UpdatedDate)
            .Take(MaxSuggestions)
            .Select(x => new TagSuggestion(x.Id, x.Score, x.Reasons))
            .ToList();
    }

    // Kept as a named query so a test can compile it through the SQL Server provider.
    internal static IQueryable<RankCandidate> CandidatesQuery(PrintLogContext db, long userId) =>
        db.Filaments.AsNoTracking()
            .Where(f => f.CreatedById == userId && f.IsActive)
            .Select(f => new RankCandidate(
                f.Id,
                f.Brand,
                f.MaterialType,
                f.Colors,
                f.ColorHex,
                f.InitialNominalWeightMg,
                f.Tags.Any(),
                f.UpdatedDate,
                f.ColorName,
                f.DisplayName));

    /// <summary>
    /// Scores a user's own materials against a spool label, for finding the spool by a scan. A
    /// label has a color name rather than a hex color, and whether the spool has an NFC tag says
    /// nothing about it, so those two signals are swapped for <c>same-color-name</c>.
    /// </summary>
    public async Task<IReadOnlyList<TagSuggestion>> RankAsync(long userId, LabelReading reading, CancellationToken ct)
    {
        var materialKey = MaterialKey.Normalize(reading.MaterialType);
        if (materialKey.Length == 0)
        {
            return [];
        }

        var candidates = await CandidatesQuery(db, userId).ToListAsync(ct);
        var color = reading.ColorName?.Trim();
        var brand = reading.Brand?.Trim();

        return candidates
            .Where(f => SameMaterial(materialKey, MaterialKey.Normalize(f.MaterialType)))
            .Select(f =>
            {
                var score = 0.4;
                var reasons = new List<string> { "same-material" };
                if (!string.IsNullOrEmpty(color)
                    && ((f.ColorName?.Contains(color, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (f.DisplayName?.Contains(color, StringComparison.OrdinalIgnoreCase) ?? false)))
                {
                    score += 0.25;
                    reasons.Add("same-color-name");
                }
                if (!string.IsNullOrEmpty(brand) && string.Equals(brand, f.Brand?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    score += 0.15;
                    reasons.Add("same-brand");
                }
                if (reading.NetWeightG is { } grams && f.InitialNominalWeightMg is { } mg
                    && (long)Math.Round(mg / 1000.0) == grams)
                {
                    score += 0.05;
                    reasons.Add("same-weight");
                }
                return (f.Id, f.UpdatedDate, Score: Math.Round(score, 4), Reasons: reasons);
            })
            .Where(x => x.Score >= MinScore)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.UpdatedDate)
            .Take(MaxSuggestions)
            .Select(x => new TagSuggestion(x.Id, x.Score, x.Reasons))
            .ToList();
    }

    /// <summary>Prefix match in either direction, so PLA and PLA+ agree.</summary>
    private static bool SameMaterial(string a, string b) =>
        b.Length > 0 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal));
}
