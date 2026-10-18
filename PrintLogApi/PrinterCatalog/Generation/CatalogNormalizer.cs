using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrintLogApi.PrinterCatalog.Generation;

/// <summary>
/// The dedupe key the UI's old <c>compare.ts</c> used, ported: lowercase, drop everything but
/// ASCII letters, digits and whitespace, collapse whitespace. Makes go through
/// <c>make-aliases.json</c> first, compared in normalized form, so "Creality3D" and "creality 3d"
/// both become "Creality".
/// </summary>
public sealed partial class CatalogNormalizer
{
    /// <summary>For data whose makes are already aliased, such as a generated catalog file.</summary>
    public static readonly CatalogNormalizer NoAliases = new(new Dictionary<string, string>());

    private readonly IReadOnlyList<(string NormalizedFrom, string To)> _aliases;

    public CatalogNormalizer(IReadOnlyDictionary<string, string> makeAliases)
    {
        _aliases = makeAliases.Select(a => (Normalize(a.Key), a.Value)).ToList();
    }

    /// <summary>Reads a <c>make-aliases.json</c> object of raw make to canonical make.</summary>
    public static IReadOnlyDictionary<string, string> LoadAliases(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
        ?? throw new InvalidDataException($"{path} is empty.");

    /// <summary>The canonical spelling of a make, or the make unchanged when no alias matches.</summary>
    public string ApplyAlias(string make)
    {
        var normalized = Normalize(make);
        foreach (var (from, to) in _aliases)
        {
            if (from == normalized)
            {
                return to;
            }
        }
        return make;
    }

    /// <summary><c>normalize(alias(make)) | normalize(model)</c>.</summary>
    public string Key(string make, string model) => $"{Normalize(ApplyAlias(make))}|{Normalize(model)}";

    public static string Normalize(string value)
    {
        var stripped = NotAlphanumericOrSpace().Replace(value.ToLowerInvariant(), "");
        return Whitespace().Replace(stripped, " ").Trim();
    }

    /// <summary>The normalized words of a name, for search.</summary>
    public static string[] Tokens(string value) => Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"[^a-z0-9\s]")]
    private static partial Regex NotAlphanumericOrSpace();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
