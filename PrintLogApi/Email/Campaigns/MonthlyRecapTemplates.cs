using System.Globalization;
using System.Text;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Templates.MonthlyRecap;

namespace PrintLogApi.Email.Campaigns;

/// <summary>One "did you know" line at the bottom of a recap, pointing at a feature the user hasn't tried.</summary>
public sealed record RecapTip(string Text, string Url, string Label);

/// <summary>A badge tier earned during the month, with its image (see <see cref="BadgeImage"/>).</summary>
public sealed record RecapBadge(string Title, string TierName, string ImageUrl);

/// <summary>
/// One headline number. <paramref name="Change"/> is the sentence the text part uses ("up 50% vs
/// October"); <paramref name="ShortChange"/> is what a tile shows beside its arrow ("50% vs October").
/// </summary>
public sealed record RecapStat(string Value, string Label, string? Change = null, string? ShortChange = null, Trend? Trend = null);

/// <summary>Everything a monthly recap shows. Numbers come from the analytics overview, so they match the analytics page.</summary>
/// <param name="PrintCountChangePercent">Whole-percent change against the previous month; null when that month had no prints.</param>
/// <param name="PrintHoursChangePercent">Whole-percent change in print time; null when the previous month had none.</param>
/// <param name="Cost">Already formatted, or null when nothing could be priced.</param>
/// <param name="Badges">The badge tiers earned during the month, at most <see cref="MonthlyRecapTemplates.MaxBadges"/>.</param>
/// <param name="MoreBadgeCount">How many more were earned than <paramref name="Badges"/> shows.</param>
public sealed record MonthlyRecapModel(
    string? Name,
    string MonthName,
    string PreviousMonthName,
    int PrintCount,
    double? SuccessRatePercent,
    double PrintHours,
    double FilamentGrams,
    string? Cost,
    int? PrintCountChangePercent,
    int? PrintHoursChangePercent,
    string? MostUsedPrinter,
    string? MostUsedMaterial,
    string? LongestPrint,
    IReadOnlyList<RecapBadge> Badges,
    int MoreBadgeCount,
    string AchievementsUrl,
    RecapTip? Tip,
    string StatsUrl);

public static class MonthlyRecapTemplates
{
    public const string CtaLabel = "See your full stats";

    /// <summary>
    /// Two rows of three. A month can hold dozens of tiers (a new user, or a backfill run), and
    /// every extra badge risks Gmail clipping the message at ~102 KB, footer and unsubscribe included.
    /// </summary>
    public const int MaxBadges = 6;

    public static string Subject(MonthlyRecapModel m)
        => $"Your {m.MonthName} in prints: {EmailFormat.Count(m.PrintCount, "print", "prints")}, {EmailFormat.Hours(m.PrintHours)}";

    public static async Task<(string Html, string Text)> RenderAsync(IEmailTemplateRenderer renderer, MonthlyRecapModel model, EmailFooterModel footer)
    {
        var html = await renderer.RenderAsync<MonthlyRecapEmail>(new Dictionary<string, object?> { ["Model"] = model, ["Footer"] = footer });
        return (html, Text(model, footer));
    }

    public static string Intro(MonthlyRecapModel m) => $"Here's how your {m.MonthName} went.";

    /// <summary>The headline band's title, e.g. "Your November in prints".</summary>
    public static string Headline(MonthlyRecapModel m) => $"Your {m.MonthName} in prints";

    /// <summary>"up 50% vs October", "down 12% vs October" or "same as October".</summary>
    public static string Change(int percent, string previousMonth) => percent switch
    {
        > 0 => $"up {percent.ToString(CultureInfo.InvariantCulture)}% vs {previousMonth}",
        < 0 => $"down {(-percent).ToString(CultureInfo.InvariantCulture)}% vs {previousMonth}",
        _ => $"same as {previousMonth}",
    };

    /// <summary>"50% vs October" (the tile's arrow says which way) or "same as October".</summary>
    public static string ShortChange(int percent, string previousMonth) => percent == 0
        ? $"same as {previousMonth}"
        : $"{Math.Abs(percent).ToString(CultureInfo.InvariantCulture)}% vs {previousMonth}";

    private static RecapStat WithChange(string value, string label, int? percent, string previousMonth) => percent is { } p
        ? new RecapStat(value, label, Change(p, previousMonth), ShortChange(p, previousMonth), p > 0 ? Trend.Up : p < 0 ? Trend.Down : Trend.Flat)
        : new RecapStat(value, label);

    /// <summary>The two headline numbers the hero tiles show: prints and print time.</summary>
    public static IReadOnlyList<RecapStat> HeroStats(MonthlyRecapModel m) =>
    [
        WithChange(m.PrintCount.ToString("N0", CultureInfo.InvariantCulture), m.PrintCount == 1 ? "print" : "prints", m.PrintCountChangePercent, m.PreviousMonthName),
        WithChange(EmailFormat.Hours(m.PrintHours), "of print time", m.PrintHoursChangePercent, m.PreviousMonthName),
    ];

    /// <summary>The supporting numbers the light tiles show, only those present.</summary>
    public static IReadOnlyList<RecapStat> MinorStats(MonthlyRecapModel m)
    {
        var stats = new List<RecapStat>();
        if (m.SuccessRatePercent is { } rate)
        {
            stats.Add(new RecapStat($"{Math.Round(rate, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}%", "successful"));
        }

        if (m.FilamentGrams > 0)
        {
            stats.Add(new RecapStat(EmailFormat.Weight(m.FilamentGrams), "of filament"));
        }

        if (m.Cost is { } cost)
        {
            stats.Add(new RecapStat(cost, "in material and power"));
        }

        return stats;
    }

    /// <summary>Every headline number, in display order.</summary>
    public static IReadOnlyList<RecapStat> Stats(MonthlyRecapModel m) => [.. HeroStats(m), .. MinorStats(m)];

    public static IReadOnlyList<(string Label, string Value)> Highlights(MonthlyRecapModel m)
    {
        var rows = new List<(string, string)>();
        if (m.MostUsedPrinter is { } printer) rows.Add(("Busiest printer", printer));
        if (m.MostUsedMaterial is { } material) rows.Add(("Most-used material", material));
        if (m.LongestPrint is { } longest) rows.Add(("Longest print", longest));
        return rows;
    }

    /// <summary>"…and 3 more" under the badge shelf.</summary>
    public static string MoreBadges(int count) => $"…and {count.ToString(CultureInfo.InvariantCulture)} more";

    public static string Text(MonthlyRecapModel m, EmailFooterModel footer)
    {
        var b = new StringBuilder();
        b.Append(Headline(m)).Append("\n\n");
        b.Append(EmailFormat.Greeting(m.Name)).Append("\n\n");
        b.Append(Intro(m)).Append("\n\n");

        foreach (var stat in Stats(m))
        {
            b.Append("- ").Append(stat.Value).Append(' ').Append(stat.Label);
            if (stat.Change is not null)
            {
                b.Append(" (").Append(stat.Change).Append(')');
            }

            b.Append('\n');
        }

        var highlights = Highlights(m);
        if (highlights.Count > 0)
        {
            b.Append('\n');
            foreach (var (label, value) in highlights)
            {
                b.Append(label).Append(": ").Append(value).Append('\n');
            }
        }

        if (m.Badges.Count > 0)
        {
            b.Append("\nBadges earned in ").Append(m.MonthName).Append(":\n");
            foreach (var badge in m.Badges)
            {
                b.Append("- ").Append(badge.Title).Append(" (").Append(badge.TierName).Append(")\n");
            }

            if (m.MoreBadgeCount > 0)
            {
                b.Append(MoreBadges(m.MoreBadgeCount)).Append(": ").Append(m.AchievementsUrl).Append('\n');
            }
        }

        b.Append('\n').Append(CtaLabel).Append(": ").Append(m.StatsUrl).Append('\n');

        if (m.Tip is { } tip)
        {
            b.Append("\nTip: ").Append(tip.Text).Append('\n');
            b.Append(tip.Label).Append(": ").Append(tip.Url).Append('\n');
        }

        return b.Append(OnboardingTemplates.TextFooter(footer)).ToString();
    }
}
