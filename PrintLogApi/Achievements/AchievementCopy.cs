using System.Globalization;

namespace PrintLogApi.Achievements;

/// <summary>Fills the catalog's copy templates.</summary>
public static class AchievementCopy
{
    /// <summary>
    /// Replaces <c>{n}</c> with <paramref name="n"/> grouped by thousands ("1,000") and <c>{s}</c>
    /// with "s" unless <paramref name="n"/> is 1.
    /// </summary>
    public static string Format(string template, long n) => template
        .Replace("{n}", n.ToString("N0", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{s}", n == 1 ? "" : "s", StringComparison.Ordinal);
}
