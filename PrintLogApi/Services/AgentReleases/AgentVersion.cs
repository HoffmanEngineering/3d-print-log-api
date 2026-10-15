using System.Globalization;
using System.Text.RegularExpressions;

namespace PrintLogApi.Services.AgentReleases;

/// <summary>Compares the versions agents report, which are <c>X.Y.Z</c> with maybe a suffix.</summary>
public static partial class AgentVersion
{
    /// <summary>
    /// Whether <paramref name="current"/> is older than <paramref name="latest"/>. False when either
    /// is missing or not a version, so an agent that reports something unexpected is never told to
    /// update. Only the first three numbers count: <c>0.2.0.dev1</c> is <c>0.2.0</c>.
    /// </summary>
    public static bool IsOlder(string? current, string? latest)
        => Parse(current) is { } have && Parse(latest) is { } newest && have < newest;

    /// <summary>The leading <c>X.Y.Z</c> of <paramref name="text"/> (a <c>v</c> before it is allowed), or null.</summary>
    public static Version? Parse(string? text)
    {
        if (text is null || Leading().Match(text) is not { Success: true } match)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            && int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
            ? new Version(major, minor, patch)
            : null;
    }

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)(?![\d])")]
    private static partial Regex Leading();
}
