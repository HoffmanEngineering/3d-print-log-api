using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PrintLogApi.Models;

namespace PrintLogApi.PrinterCatalog;

/// <summary>A slicer printer name resolved to one catalog model (spec §6.7).</summary>
public sealed record CatalogMatch(string Id, string Confidence)
{
    public const string Exact = "exact";
    public const string Alias = "alias";
    public const string Fuzzy = "fuzzy";
}

/// <summary>
/// The in-memory printer catalog (spec §6.4). Search, popular and match offer only visible,
/// non-retired models; <see cref="Get"/> resolves every row, because linked printers keep retired
/// and hidden ids. The returned models are shared by every caller: treat them as read-only.
/// </summary>
public interface IPrinterCatalogIndex
{
    /// <summary>Changes whenever a rebuild changes what any read could return. For ETags.</summary>
    string Version { get; }

    /// <summary>False until the first <see cref="Rebuild"/>: an empty index then means "not loaded yet", not "no models".</summary>
    bool IsReady { get; }

    /// <summary>Replaces the whole index. Reads running meanwhile finish on the previous one.</summary>
    void Rebuild(IEnumerable<PrinterModel> models);

    /// <summary>The best <paramref name="limit"/> matches, or the popular list for an empty query.</summary>
    IReadOnlyList<PrinterModel> Search(string? query, int limit);

    /// <summary>
    /// Models with at least <paramref name="minOwners"/> recent owners, most first, then the
    /// featured models in their order, up to <paramref name="limit"/> (spec §6.5).
    /// </summary>
    IReadOnlyList<PrinterModel> Popular(int limit, int minOwners);

    /// <summary>The model with this id, retired or hidden included, or null.</summary>
    PrinterModel? Get(string id);

    /// <summary>The one model a slicer's printer name stands for, or null when none or several fit.</summary>
    CatalogMatch? Match(string name);
}

/// <summary>
/// Holds one immutable <see cref="Snapshot"/> and swaps it whole on <see cref="Rebuild"/>, so a read
/// never sees a half-built index and needs no lock.
/// </summary>
public sealed partial class PrinterCatalogIndex(IOptions<PrinterCatalogOptions> options) : IPrinterCatalogIndex
{
    private readonly int _minOwners = options.Value.PopularityMinOwners;
    private readonly Lock _rebuild = new();
    private volatile Snapshot _snapshot = Snapshot.Build([]);
    private volatile bool _isReady;

    public string Version => _snapshot.Version;

    public bool IsReady => _isReady;

    /// <summary>
    /// Serialized, so two rebuilds (the seeder and the popularity job) swap in the order they ran
    /// rather than whichever finished building last.
    /// </summary>
    public void Rebuild(IEnumerable<PrinterModel> models)
    {
        lock (_rebuild)
        {
            _snapshot = Snapshot.Build(models);
            _isReady = true;
        }
    }

    public IReadOnlyList<PrinterModel> Search(string? query, int limit)
    {
        var snapshot = _snapshot;
        if (limit <= 0)
        {
            return [];
        }
        var parsed = Query.Parse(query);
        if (parsed == null)
        {
            return Popular(snapshot, limit, _minOwners);
        }

        // Visible is already in rank order, and OrderBy is stable, so sorting by tier alone keeps
        // the within-tier order: OwnerCount, then Featured, then make and model.
        return snapshot.Visible
            .Select(entry => (entry, tier: entry.TierFor(parsed)))
            .Where(x => x.tier != Tier.None)
            .OrderBy(x => x.tier == Tier.Alias ? Tier.Exact : x.tier)
            .Take(limit)
            .Select(x => x.entry.Model)
            .ToList();
    }

    public IReadOnlyList<PrinterModel> Popular(int limit, int minOwners) => Popular(_snapshot, limit, minOwners);

    public PrinterModel? Get(string id) => _snapshot.ById.GetValueOrDefault(id);

    public CatalogMatch? Match(string name)
    {
        var parsed = Query.Parse(CleanSlicerName(name));
        if (parsed == null)
        {
            return null;
        }
        // Tiers 1, 4 and 5 only: a model the prefix tiers would take still counts for tier 4, so
        // "Prusa" is ambiguous across every Prusa rather than one model with "Prusa" in its name.
        var tiers = _snapshot.Visible.Select(entry => (entry, tier: entry.TierFor(parsed, prefixTiers: false))).ToList();

        var exact = tiers.Where(x => x.tier is Tier.Exact or Tier.Alias).ToList();
        if (exact.Count > 0)
        {
            return exact.Count == 1
                ? new CatalogMatch(exact[0].entry.Model.Id, exact[0].tier == Tier.Exact ? CatalogMatch.Exact : CatalogMatch.Alias)
                : null;
        }

        foreach (var tier in new[] { Tier.Contains, Tier.Fuzzy })
        {
            var hits = tiers.Where(x => x.tier == tier).Take(2).ToList();
            if (hits.Count > 0)
            {
                return hits.Count == 1 ? new CatalogMatch(hits[0].entry.Model.Id, CatalogMatch.Fuzzy) : null;
            }
        }
        return null;
    }

    private static List<PrinterModel> Popular(Snapshot snapshot, int limit, int minOwners)
    {
        if (limit <= 0)
        {
            return [];
        }
        var ranked = snapshot.Visible.Where(e => e.Model.OwnerCount >= minOwners).Select(e => e.Model).ToList();
        var taken = ranked.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return ranked
            .Concat(snapshot.Featured.Where(m => !taken.Contains(m.Id)))
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Strips what slicers add to a printer's name (spec §6.7): surrounding quotes, a nozzle suffix,
    /// a "(Copy)" or "- Copy" suffix, the " - 3DPrintLog" preset decoration, and PrusaSlicer's
    /// leading "Original ".
    /// </summary>
    internal static string CleanSlicerName(string? name)
    {
        var cleaned = (name ?? "").Trim();
        // The UI strips one pair of quotes; a name cut mid-string can still carry a lone one.
        cleaned = cleaned.Trim('"', '\'').Trim();

        string previous;
        do
        {
            previous = cleaned;
            cleaned = CopySuffix().Replace(cleaned, "");
            cleaned = PresetDecoration().Replace(cleaned, "");
            cleaned = NozzleSuffix().Replace(cleaned, " ").Trim();
        }
        while (cleaned != previous);

        return OriginalPrefix().Replace(cleaned, "");
    }

    [GeneratedRegex(@"\s*\(?\b\d\.\d+\s*(mm\s*)?nozzle\)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NozzleSuffix();

    [GeneratedRegex(@"\s*(\(\s*copy(\s*\d+)?\s*\)|-\s*copy(\s*\d+)?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopySuffix();

    [GeneratedRegex(@"\s+-\s+3d\s*print\s*log\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PresetDecoration();

    [GeneratedRegex(@"^original\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OriginalPrefix();

    /// <summary>
    /// Lowercase, with "-", "_" and "." as spaces and runs of whitespace collapsed (spec §6.4), so
    /// "Ender-3 V3 SE" reads "ender 3 v3 se".
    /// </summary>
    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            var mapped = c is '-' or '_' or '.' || char.IsWhiteSpace(c) ? ' ' : c;
            if (mapped != ' ' || (builder.Length > 0 && builder[^1] != ' '))
            {
                builder.Append(mapped);
            }
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>The no-space variant, so "ender3v3se" meets "Ender-3 V3 SE".</summary>
    internal static string Compact(string normalized) => normalized.Replace(" ", "");

    /// <summary>Damerau–Levenshtein (optimal string alignment) distance of at most one.</summary>
    internal static bool WithinOneEdit(string a, string b)
    {
        if (a == b)
        {
            return true;
        }
        if (Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }
        if (a.Length == b.Length)
        {
            // One substitution, or one swap of neighbors.
            return a.AsSpan(prefix + 1).SequenceEqual(b.AsSpan(prefix + 1))
                || (prefix + 1 < a.Length && a[prefix] == b[prefix + 1] && a[prefix + 1] == b[prefix]
                    && a.AsSpan(prefix + 2).SequenceEqual(b.AsSpan(prefix + 2)));
        }
        // One insertion or deletion.
        var (shorter, longer) = a.Length < b.Length ? (a, b) : (b, a);
        return shorter.AsSpan(prefix).SequenceEqual(longer.AsSpan(prefix + 1));
    }

    /// <summary>
    /// Score tiers, best first (spec §6.4). Exact and Alias are both tier 1, ranked together; they
    /// differ only in the confidence a match reports.
    /// </summary>
    private enum Tier
    {
        None = 0,
        Exact = 1,
        Alias = 2,
        ModelPrefix = 3,
        MakeThenModel = 4,
        Contains = 5,
        Fuzzy = 6,
    }

    /// <summary>
    /// One way to read a query as a make followed by a model: the first tokens and the rest, each
    /// with its compact form. Built once per query, not once per model.
    /// </summary>
    private sealed record Split(string Make, string MakeCompact, string? Model, string? ModelCompact);

    private sealed record Query(string Normalized, string Compact, string[] Tokens, Split[] Splits)
    {
        public static Query? Parse(string? text)
        {
            var normalized = Normalize(text ?? "");
            if (normalized.Length == 0)
            {
                return null;
            }
            var tokens = normalized.Split(' ');
            var splits = Enumerable.Range(1, tokens.Length).Select(split =>
            {
                var make = string.Join(' ', tokens, 0, split);
                var model = split == tokens.Length ? null : string.Join(' ', tokens, split, tokens.Length - split);
                return new Split(make, PrinterCatalogIndex.Compact(make), model, model == null ? null : PrinterCatalogIndex.Compact(model));
            }).ToArray();
            return new Query(normalized, PrinterCatalogIndex.Compact(normalized), tokens, splits);
        }
    }

    private sealed class Entry
    {
        private readonly string _make;
        private readonly string _makeCompact;
        private readonly string _model;
        private readonly string _modelCompact;
        private readonly HashSet<string> _exactKeys;
        private readonly HashSet<string> _aliasKeys;
        private readonly string _haystack;
        private readonly string _haystackCompact;
        private readonly string[] _tokens;

        public Entry(PrinterModel model)
        {
            Model = model;
            _make = Normalize(model.Make ?? "");
            _makeCompact = Compact(_make);
            _model = Normalize(model.Model ?? "");
            _modelCompact = Compact(_model);
            var aliases = ReadAliases(model.AliasesJson).Select(Normalize).Where(a => a.Length > 0).ToList();

            _exactKeys = [_modelCompact, _makeCompact + _modelCompact];
            _aliasKeys = aliases.SelectMany(a => new[] { Compact(a), _makeCompact + Compact(a) }).ToHashSet();
            _aliasKeys.ExceptWith(_exactKeys);
            _haystack = string.Join(' ', new[] { _make, _model }.Concat(aliases));
            _haystackCompact = Compact(_haystack);
            _tokens = _haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        }

        public PrinterModel Model { get; }

        public Tier TierFor(Query query, bool prefixTiers = true)
        {
            if (_exactKeys.Contains(query.Compact))
            {
                return Tier.Exact;
            }
            if (_aliasKeys.Contains(query.Compact))
            {
                return Tier.Alias;
            }
            if (prefixTiers && (_model.StartsWith(query.Normalized, StringComparison.Ordinal) || _modelCompact.StartsWith(query.Compact, StringComparison.Ordinal)))
            {
                return Tier.ModelPrefix;
            }
            if (prefixTiers && MakeThenModel(query.Splits))
            {
                return Tier.MakeThenModel;
            }
            if (query.Tokens.All(Contains))
            {
                return Tier.Contains;
            }
            if (query.Tokens.All(t => Contains(t) || (t.Length >= 4 && _tokens.Any(token => WithinOneEdit(t, token)))))
            {
                return Tier.Fuzzy;
            }
            return Tier.None;
        }

        /// <summary>The make starts with the first tokens, and the rest (if any) start the model.</summary>
        private bool MakeThenModel(Split[] splits)
        {
            foreach (var split in splits)
            {
                if (!_make.StartsWith(split.Make, StringComparison.Ordinal) && !_makeCompact.StartsWith(split.MakeCompact, StringComparison.Ordinal))
                {
                    return false;
                }
                if (split.Model == null
                    || _model.StartsWith(split.Model, StringComparison.Ordinal)
                    || _modelCompact.StartsWith(split.ModelCompact!, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private bool Contains(string token) =>
            _haystack.Contains(token, StringComparison.Ordinal) || _haystackCompact.Contains(token, StringComparison.Ordinal);

        private static string[] ReadAliases(string? json)
        {
            try
            {
                return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<string?[]>(json)?.OfType<string>().ToArray() ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    private sealed class Snapshot
    {
        /// <summary>Every row by id, retired and hidden included.</summary>
        public required IReadOnlyDictionary<string, PrinterModel> ById { get; init; }

        /// <summary>Offerable models in rank order: OwnerCount desc, Featured asc, make, model.</summary>
        public required Entry[] Visible { get; init; }

        /// <summary>Offerable featured models in their featured order.</summary>
        public required PrinterModel[] Featured { get; init; }

        public required string Version { get; init; }

        public static Snapshot Build(IEnumerable<PrinterModel> models)
        {
            // Detached copies: the index never holds an object a DbContext tracks or a caller still has.
            var all = models.Select(m => m.DetachedCopy()).ToList();
            var visible = all
                .Where(m => !m.Hidden && !m.Retired)
                .OrderByDescending(m => m.OwnerCount)
                .ThenBy(m => m.Featured ?? int.MaxValue)
                .ThenBy(m => m.Make, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Id, StringComparer.Ordinal)
                .ToList();

            return new Snapshot
            {
                ById = all.ToDictionary(m => m.Id, StringComparer.Ordinal),
                Visible = visible.Select(m => new Entry(m)).ToArray(),
                Featured = visible.Where(m => m.Featured != null).OrderBy(m => m.Featured).ToArray(),
                Version = ComputeVersion(all),
            };
        }

        /// <summary>
        /// A hash of what the reads depend on. <see cref="PrinterModel.UpdatedAt"/> moves with every
        /// seeded change; the owner count and the flags are hashed directly because the popularity
        /// job and an operator change them without touching it.
        /// </summary>
        private static string ComputeVersion(List<PrinterModel> models)
        {
            var text = new StringBuilder();
            foreach (var m in models.OrderBy(m => m.Id, StringComparer.Ordinal))
            {
                text.Append(m.Id).Append('|').Append(m.UpdatedAt.UtcTicks).Append('|').Append(m.OwnerCount)
                    .Append('|').Append(m.Hidden ? '1' : '0').Append(m.Retired ? '1' : '0').Append('\n');
            }
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
        }
    }
}
