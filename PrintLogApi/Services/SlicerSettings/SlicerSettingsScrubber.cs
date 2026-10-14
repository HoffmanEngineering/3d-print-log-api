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

    public static bool IsSensitiveKey(string key)
    {
        var lower = key.Trim().ToLowerInvariant();
        return SensitiveKeys.Contains(lower) || SensitiveFragments.Any(lower.Contains);
    }

    /// <summary>
    /// Blanks the value of every <c>; key = value</c> (or <c>; key: value</c>) comment line whose
    /// key is sensitive, keeping the key so the line still parses as an empty setting. Every other
    /// byte is unchanged. Linear in the length of the text.
    /// </summary>
    public static string ScrubText(string text)
    {
        StringBuilder? output = null;
        var copiedTo = 0;
        var lineStart = 0;
        while (lineStart < text.Length)
        {
            var newline = text.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? text.Length : newline;

            if (SensitiveValue(text, lineStart, lineEnd) is var (valueStart, valueEnd) && valueEnd > valueStart)
            {
                output ??= new StringBuilder(text.Length);
                output.Append(text, copiedTo, valueStart - copiedTo);
                copiedTo = valueEnd;
            }

            lineStart = lineEnd + 1;
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
    /// The span of the value on a <c>; key = value</c> line with a sensitive key, excluding a
    /// trailing <c>\r</c>; null for any other line.
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

        var valueEnd = lineEnd > separator + 1 && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
        return (separator + 1, valueEnd);
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
