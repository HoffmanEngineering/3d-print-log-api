using System.Buffers;
using System.Text;

namespace PrintLogApi.Services.SlicerSettings;

/// <summary>
/// Removes the values of host and credential settings from slicer settings a connector sends
/// (#164). A slicer writes its whole configuration into the G-code, and that includes how it
/// reaches the printer: <c>print_host</c>, <c>printhost_apikey</c>, <c>printhost_password</c> and
/// so on. None of it belongs in a print log.
/// <para>
/// The text is scrubbed <em>before</em> it is parsed, so nothing derived from it (the stored
/// settings, the rendered note, any diagnostic) ever sees a value. The parsed settings are
/// scrubbed again as a second layer. The raw text itself is never stored.
/// </para>
/// </summary>
public static class SlicerSettingsScrubber
{
    // Matched anywhere in the lower-cased key. "user" and "token" are broad on purpose: no slicer
    // setting the note templates read contains either, and a false positive costs one setting.
    // Not "url" ("slowdown_for_curled_perimeters") and not "host" ("host_type" is in Orca's note).
    private static readonly string[] SensitiveFragments =
    [
        "print_host", "printhost", "apikey", "api_key", "password", "passwd", "token", "secret",
        "user", "cookie", "credential", "authorization", "access_code",
    ];

    // Whole keys. post_process holds a script's command line, which can carry a key or a token as
    // an argument, and always carries a local path with the user's account name in it.
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase) { "post_process" };

    /// <summary>
    /// Every character <see cref="MemoryExtensions.EnumerateLines(ReadOnlySpan{char})"/> ends a line
    /// at, which is how the parser library reads the text. Splitting on fewer would leave, say, a
    /// CR-only block as one "line" here and many lines there: unscrubbed, then parsed.
    /// </summary>
    private static readonly SearchValues<char> LineBreaks = SearchValues.Create("\r\n\f\u0085\u2028\u2029");

    public static bool IsSensitiveKey(string key)
    {
        var lower = key.Trim().ToLowerInvariant();
        return SensitiveKeys.Contains(lower) || SensitiveFragments.Any(lower.Contains);
    }

    /// <summary>
    /// Blanks the value of every <c>; key = value</c> (or <c>; key: value</c>) comment line whose
    /// key is sensitive, keeping the key so the line still parses as an empty setting. Every other
    /// character is unchanged. Lines end where the parser's do. Linear in the length of the text.
    /// </summary>
    public static string ScrubText(string text)
    {
        StringBuilder? output = null;
        var copiedTo = 0;
        var lineStart = 0;
        while (lineStart < text.Length)
        {
            var lineBreak = text.AsSpan(lineStart).IndexOfAny(LineBreaks);
            var lineEnd = lineBreak < 0 ? text.Length : lineStart + lineBreak;

            if (SensitiveValue(text, lineStart, lineEnd) is var (valueStart, valueEnd) && valueEnd > valueStart)
            {
                output ??= new StringBuilder(text.Length);
                output.Append(text, copiedTo, valueStart - copiedTo);
                copiedTo = valueEnd;
            }

            // "\r\n" is one break, as in EnumerateLines; an empty line between them is not a line.
            lineStart = lineEnd + (lineEnd + 1 < text.Length && text[lineEnd] == '\r' && text[lineEnd + 1] == '\n' ? 2 : 1);
        }

        if (output is null)
        {
            return text;
        }

        output.Append(text, copiedTo, text.Length - copiedTo);
        return output.ToString();
    }

    /// <summary>The settings less every sensitive key.</summary>
    public static SortedDictionary<string, string> ScrubSettings(IEnumerable<KeyValuePair<string, string>> settings)
    {
        var scrubbed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in settings)
        {
            if (!IsSensitiveKey(key))
            {
                scrubbed[key] = value;
            }
        }

        return scrubbed;
    }

    /// <summary>
    /// The span of the value on a <c>; key = value</c> line with a sensitive key; null for any other
    /// line. <paramref name="lineEnd"/> excludes the line break.
    /// </summary>
    private static (int Start, int End)? SensitiveValue(string text, int lineStart, int lineEnd)
    {
        var i = SkipSpaces(text, lineStart, lineEnd);
        if (i >= lineEnd || text[i] != ';')
        {
            return null;
        }

        var keyStart = SkipSpaces(text, i + 1, lineEnd);
        var keyEnd = keyStart;
        while (keyEnd < lineEnd && (char.IsAsciiLetterOrDigit(text[keyEnd]) || text[keyEnd] is '_' or '-' or '.'))
        {
            keyEnd++;
        }

        var separator = SkipSpaces(text, keyEnd, lineEnd);
        if (keyEnd == keyStart || separator >= lineEnd || text[separator] is not ('=' or ':'))
        {
            return null;
        }

        if (!IsSensitiveKey(text[keyStart..keyEnd]))
        {
            return null;
        }

        return (separator + 1, lineEnd);
    }

    private static int SkipSpaces(string text, int index, int end)
    {
        while (index < end && text[index] is ' ' or '\t')
        {
            index++;
        }

        return index;
    }
}
