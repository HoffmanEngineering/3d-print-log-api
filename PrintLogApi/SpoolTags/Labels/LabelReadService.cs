using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.SpoolTags;
using PrintLogApi.Services;
using PrintLogApi.SpoolTags.Matching;

namespace PrintLogApi.SpoolTags.Labels;

/// <summary>
/// Resolves a spool label read (<c>POST /api/Filaments/label-reads</c>): finds the brand and color
/// name in the OCR text, then the user's spools it may be (a stored barcode first, then a ranked
/// match) and the catalog entry it may be (a trusted barcode vote first, then by name).
/// </summary>
public sealed class LabelReadService(
    PrintLogContext db,
    LabelVocabulary vocabulary,
    FilamentCatalogMatcher matcher,
    TagSuggestionRanker ranker)
{
    public const int MaxBarcodes = 8;
    public const int MaxOcrChars = 4000;

    public async Task<LabelReadResponse> ResolveAsync(long userId, LabelReadRequestDto request, CancellationToken ct)
    {
        var barcodes = (request.Barcodes ?? [])
            .Take(MaxBarcodes)
            .Select(b => (Input: b, Value: BarcodeValue.Normalize(b.Value)))
            .Where(b => b.Value != null)
            .DistinctBy(b => b.Value)
            .Select(b => new LabelBarcode(b.Value!, BarcodeValue.NormalizeFormat(b.Input.Format, b.Value!)))
            .ToList();
        var text = request.OcrText is { Length: > MaxOcrChars } ocr ? ocr[..MaxOcrChars] : request.OcrText ?? "";

        var reading = await ReadAsync(userId, text, request.Fields, ct);
        var values = barcodes.Select(b => b.Value).ToList();

        var own = new List<LabelOwnMatch>();
        if (values.Count > 0)
        {
            var byBarcode = await OwnByBarcodeQuery(db, userId, values).ToListAsync(ct);
            own.AddRange(byBarcode.Select(id => new LabelOwnMatch(id, 1.0, ["same-barcode"])));
        }
        foreach (var s in await ranker.RankAsync(userId, reading, ct))
        {
            if (own.Count >= TagSuggestionRanker.MaxSuggestions)
            {
                break;
            }
            if (own.All(o => o.FilamentId != s.FilamentId))
            {
                own.Add(new LabelOwnMatch(s.FilamentId, s.Score, s.Reasons));
            }
        }

        CatalogMatch? catalogMatch = null;
        foreach (var value in values)
        {
            if (await matcher.TrustedEntryIdAsync(FilamentService.BarcodeVoteFormat, value, ct) is { } entryId
                && await matcher.EntryAsync(entryId, ct) is { } trusted)
            {
                catalogMatch = trusted;
                break;
            }
        }
        catalogMatch ??= await matcher.MatchByNameAsync(reading, ct);

        return new LabelReadResponse(reading, catalogMatch, own.Take(TagSuggestionRanker.MaxSuggestions).ToList(), barcodes);
    }

    private async Task<LabelReading> ReadAsync(long userId, string text, LabelFieldsDto? fields, CancellationToken ct)
    {
        string? brand = null;
        string? color = null;
        if (text.Length > 0)
        {
            var mine = await db.Filaments.AsNoTracking()
                .Where(f => f.CreatedById == userId && f.IsActive)
                .Select(f => new { f.Brand, f.ColorName })
                .Distinct()
                .ToListAsync(ct);
            var catalog = await vocabulary.GetAsync(ct);

            brand = LabelTextMatcher.FindBrand(text,
                mine.Select(m => m.Brand).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                catalog.Brands);

            // The brand's catalog names are the likeliest color names; the user's own come next.
            var names = new List<string>();
            if (brand != null && catalog.NamesByBrand.TryGetValue(brand, out var brandNames))
            {
                names.AddRange(brandNames);
            }
            names.AddRange(mine.Select(m => m.ColorName).OfType<string>());
            color = LabelTextMatcher.FindColor(text, names.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        return new LabelReading(
            brand,
            fields?.MaterialType?.Trim() is { Length: > 0 } material ? material : null,
            color,
            fields?.WeightG,
            fields?.DiameterMm,
            fields?.HotendMinC,
            fields?.HotendMaxC,
            fields?.BedMinC,
            fields?.BedMaxC);
    }

    // Kept as a named query so a test can compile it through the SQL Server provider.
    internal static IQueryable<Guid> OwnByBarcodeQuery(PrintLogContext db, long userId, List<string> values) =>
        db.FilamentBarcodes.AsNoTracking()
            .Where(b => values.Contains(b.Value) && b.Filament.CreatedById == userId)
            .Select(b => new { b.FilamentId, b.Filament.UpdatedDate })
            .Distinct()
            .OrderByDescending(x => x.UpdatedDate)
            .Take(TagSuggestionRanker.MaxSuggestions)
            .Select(x => x.FilamentId);
}
