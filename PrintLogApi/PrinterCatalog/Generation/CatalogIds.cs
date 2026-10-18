using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PrintLogApi.PrinterCatalog.Generation;

/// <summary>
/// Stable catalog ids. A published id never changes. Ids follow a model's raw source identities
/// (<see cref="CatalogModel.SourceKeys"/>, taken before any alias, make-prefix stripping or
/// override), so neither a new alias, a rename in a source, nor a name override moves an id.
/// <list type="bullet">
/// <item>A model takes back the id of a previous model or reserved entry that shares any of its
/// source keys. When several match, the lowest id wins, and models claim in the order of their
/// sorted keys, so conflicts resolve the same way every run.</item>
/// <item>The normalized make|model key is only a fallback, for previous entries with no source
/// keys: a hand-added override extra (or a file written before source keys existed). A source
/// model with the extra's names adopts its id.</item>
/// <item>A new model gets the slug of its make and model, avoiding every id the previous file
/// holds in models or reserved, with a numeric suffix on a collision.</item>
/// </list>
/// </summary>
public static partial class CatalogIds
{
    private const int MaxSlugLength = 90;

    public static IReadOnlyList<CatalogModel> Assign(
        IEnumerable<CatalogModel> merged, CatalogFile? previous, CatalogNormalizer? normalizer = null, Action<string>? log = null)
    {
        normalizer ??= CatalogNormalizer.NoAliases;
        var models = merged.ToList();
        var ids = new string?[models.Count];

        var taken = new HashSet<string>(StringComparer.Ordinal);
        var idsBySourceKey = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var idsByNameKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var extras = new HashSet<string>(StringComparer.Ordinal);

        void Remember(string id, IReadOnlyList<string>? sourceKeys)
        {
            taken.Add(id);
            foreach (var key in sourceKeys ?? [])
            {
                if (!idsBySourceKey.TryGetValue(key, out var set))
                {
                    idsBySourceKey[key] = set = new SortedSet<string>(StringComparer.Ordinal);
                }
                set.Add(id);
            }
        }

        foreach (var old in (previous?.Models ?? []).OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            Remember(old.Id, old.SourceKeys);
            if (old.SourceKeys is not { Count: > 0 })
            {
                extras.Add(old.Id);
                idsByNameKey.TryAdd(normalizer.Key(old.Make, old.Model), old.Id);
            }
        }
        foreach (var reserved in previous?.Reserved ?? [])
        {
            Remember(reserved.Id, reserved.SourceKeys);
        }

        // Keep published ids first, so a new model can never take one before its owner does.
        var reused = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in Enumerable.Range(0, models.Count).OrderBy(i => ClaimOrder(models[i]), StringComparer.Ordinal))
        {
            var match = (models[i].SourceKeys ?? [])
                .SelectMany(k => idsBySourceKey.GetValueOrDefault(k) ?? [])
                .Where(id => !reused.Contains(id))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
            if (match is null
                && idsByNameKey.TryGetValue(normalizer.Key(models[i].Make, models[i].Model), out var byName)
                && !reused.Contains(byName))
            {
                match = byName;
            }
            if (match is not null)
            {
                reused.Add(match);
                ids[i] = match;
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
            if (candidate != slug && extras.Contains(slug))
            {
                log?.Invoke(
                    $"ids: \"{slug}\" belongs to an override extra whose make and model differ from " +
                    $"{models[i].Make} | {models[i].Model}, so its id was not adopted; the source model gets \"{candidate}\" " +
                    "and both stay. Rename the extra to match, or remove it, to merge them.");
            }
            taken.Add(candidate);
            ids[i] = candidate;
        }

        return models.Select((m, i) => m with { Id = ids[i]! }).ToList();
    }

    // Models with source keys claim by their first key; name-only models (extras) go last.
    private static string ClaimOrder(CatalogModel m) =>
        m.SourceKeys is { Count: > 0 } keys ? keys[0] : "~" + m.Make + "|" + m.Model;

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
