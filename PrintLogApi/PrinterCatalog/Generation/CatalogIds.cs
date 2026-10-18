using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PrintLogApi.PrinterCatalog.Generation;

/// <summary>
/// Stable catalog ids. A published id never changes. Ids follow a model's source key (see
/// <see cref="CatalogModel.SourceKey"/>, the normalized source make and model before any
/// override), so a rename in the overrides or a punctuation change in the sources keeps the id.
/// A model whose key matches one in the previous file's models or reserved list takes that id
/// back. A new model gets the slug of its make and model, avoiding every id either list holds,
/// with a numeric suffix on a collision.
/// </summary>
public static partial class CatalogIds
{
    private const int MaxSlugLength = 90;

    public static IReadOnlyList<CatalogModel> Assign(
        IEnumerable<CatalogModel> merged, CatalogFile? previous, CatalogNormalizer? normalizer = null)
    {
        normalizer ??= CatalogNormalizer.NoAliases;
        var models = merged.Select(m => m with { SourceKey = m.SourceKey ?? normalizer.Key(m.Make, m.Model) }).ToList();
        var ids = new string?[models.Count];

        var taken = new HashSet<string>(StringComparer.Ordinal);
        var previousByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var old in previous?.Models ?? [])
        {
            taken.Add(old.Id);
            // A file written before source keys existed: its names are the best key there is.
            previousByKey.TryAdd(old.SourceKey ?? normalizer.Key(old.Make, old.Model), old.Id);
        }
        foreach (var reserved in previous?.Reserved ?? [])
        {
            taken.Add(reserved.Id);
            if (reserved.SourceKey is not null)
            {
                previousByKey.TryAdd(reserved.SourceKey, reserved.Id);
            }
        }

        // Keep published ids first, so a new model can never take one before its owner does.
        var reused = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < models.Count; i++)
        {
            if (previousByKey.TryGetValue(models[i].SourceKey!, out var id) && reused.Add(id))
            {
                ids[i] = id;
            }
        }

        // Then new models, in make/model order, so suffixes don't depend on file order.
        foreach (var i in Enumerable.Range(0, models.Count)
                     .Where(i => ids[i] is null)
                     .OrderBy(i => models[i].Make, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(i => models[i].Model, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(i => models[i].Make, StringComparer.Ordinal)
                     .ThenBy(i => models[i].Model, StringComparer.Ordinal))
        {
            var slug = Slug(models[i].Make, models[i].Model);
            var candidate = slug;
            for (var n = 2; taken.Contains(candidate); n++)
            {
                candidate = $"{slug}-{n}";
            }
            taken.Add(candidate);
            ids[i] = candidate;
        }

        return models.Select((m, i) => m with { Id = ids[i]! }).ToList();
    }

    /// <summary>
    /// <c>Bambu Lab</c> + <c>P1S</c> is <c>bambu-lab-p1s</c>, and <c>MINI+</c> ends in
    /// <c>-mini-plus</c>. Accents are folded to ASCII.
    /// </summary>
    public static string Slug(string make, string model)
    {
        var decomposed = CatalogNormalizer.SpellPlus($"{make} {model}").Normalize(NormalizationForm.FormD);
        var ascii = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                ascii.Append(c);
            }
        }
        var slug = NotSlugChars().Replace(ascii.ToString().ToLowerInvariant(), "-").Trim('-');
        // PrinterModel.Id is 100 characters; leave room for a collision suffix.
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength].TrimEnd('-');
        }
        return slug.Length == 0 ? "printer" : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlugChars();
}
