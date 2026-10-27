using System.Globalization;
using System.Text;

namespace PrintLogApi.SpoolTags.Labels;

/// <summary>
/// Finds a known brand or color name in OCR text from a spool label. OCR mangles letters and glues
/// words, so a name of five or more characters may differ by one character per five; shorter
/// names must match exactly. The longest match wins, so "Elegoo Rapid" beats "Elegoo".
/// </summary>
public static class LabelTextMatcher
{
    private const int FuzzyMinLength = 5;

    /// <summary>
    /// The brand the text names, from the user's own brands first, then the catalog's. Null when
    /// none appears.
    /// </summary>
    public static string? FindBrand(string text, IReadOnlyList<string> userBrands, IReadOnlyList<string> catalogBrands)
    {
        var words = Words(text);
        return Best(words, userBrands.Select(b => (Name: b, Rank: 1)).Concat(catalogBrands.Select(b => (Name: b, Rank: 0))));
    }

    /// <summary>The longest of <paramref name="names"/> the text contains. Exact words only: colors are short and alike.</summary>
    public static string? FindColor(string text, IReadOnlyList<string> names)
    {
        var padded = " " + string.Join(' ', Words(text)) + " ";
        return names
            .Where(n => Normalize(n).Length > 0 && padded.Contains(" " + Normalize(n) + " ", StringComparison.Ordinal))
            .OrderByDescending(n => Normalize(n).Length)
            .ThenBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string? Best(string[] words, IEnumerable<(string Name, int Rank)> candidates)
    {
        string? best = null;
        (double Score, int Length, int Rank) bestKey = default;
        foreach (var (name, rank) in candidates)
        {
            var phrase = Normalize(name);
            if (phrase.Length < 2)
            {
                continue;
            }
            var score = Find(words, phrase);
            var key = (score, phrase.Length, rank);
            if (score > 0 && (best == null || key.CompareTo(bestKey) > 0))
            {
                best = name;
                bestKey = key;
            }
        }
        return best;
    }

    /// <summary>1 for an exact phrase, less for a fuzzy one, 0 when absent.</summary>
    private static double Find(string[] words, string phrase)
    {
        var parts = phrase.Split(' ');
        var allowed = phrase.Replace(" ", "").Length >= FuzzyMinLength ? phrase.Length / 5 : 0;
        var best = 0.0;
        for (var i = 0; i + parts.Length <= words.Length; i++)
        {
            var window = string.Join(' ', words, i, parts.Length);
            if (window == phrase)
            {
                return 1;
            }
            if (allowed > 0)
            {
                var distance = Levenshtein(window, phrase, allowed);
                if (distance <= allowed)
                {
                    best = Math.Max(best, 1 - (double)distance / phrase.Length);
                }
            }
        }
        return best;
    }

    private static string[] Words(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Lowercase, accents stripped, anything not a letter, digit or '+' becomes a space.</summary>
    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            var lower = char.ToLowerInvariant(c);
            sb.Append(lower is >= 'a' and <= 'z' or >= '0' and <= '9' or '+' ? lower : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Edit distance, giving up (returning max + 1) once every path exceeds <paramref name="max"/>.</summary>
    private static int Levenshtein(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max)
        {
            return max + 1;
        }
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                rowMin = Math.Min(rowMin, current[j]);
            }
            if (rowMin > max)
            {
                return max + 1;
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
