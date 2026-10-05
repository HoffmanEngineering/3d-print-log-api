using System.Globalization;
using System.Text;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Templates.MonthlyRecap;

namespace PrintLogApi.Email.Campaigns;

/// <summary>One "did you know" line at the bottom of a recap, pointing at a feature the user hasn't tried.</summary>
public sealed record RecapTip(string Text, string Url, string Label);

/// <summary>Everything a monthly recap shows. Numbers come from the analytics overview, so they match the analytics page.</summary>
/// <param name="PrintCountChangePercent">Whole-percent change against the previous month; null when that month had no prints.</param>
/// <param name="PrintHoursChangePercent">Whole-percent change in print time; null when the previous month had none.</param>
/// <param name="Cost">Already formatted, or null when nothing could be priced.</param>
/// <param name="Badges">"Title (Tier)" for each badge tier earned during the month.</param>
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
    IReadOnlyList<string> Badges,
    RecapTip? Tip,
    string StatsUrl);

public static class MonthlyRecapTemplates
{
    public const string CtaLabel = "See your full stats";

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

    /// <summary>The headline numbers, each with its optional comparison, in display order.</summary>
    public static IReadOnlyList<(string Value, string Label, string? Change)> Stats(MonthlyRecapModel m)
    {
        var stats = new List<(string, string, string?)>
        {
            (m.PrintCount.ToString("N0", CultureInfo.InvariantCulture), m.PrintCount == 1 ? "print" : "prints",
                m.PrintCountChangePercent is { } pc ? Change(pc, m.PreviousMonthName) : null),
            (EmailFormat.Hours(m.PrintHours), "of print time",
                m.PrintHoursChangePercent is { } hc ? Change(hc, m.PreviousMonthName) : null),
        };
        if (m.SuccessRatePercent is { } rate)
        {
            stats.Add(($"{Math.Round(rate, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}%", "successful", null));
        }

        if (m.FilamentGrams > 0)
        {
            stats.Add((EmailFormat.Weight(m.FilamentGrams), "of filament", null));
        }

        if (m.Cost is { } cost)
        {
            stats.Add((cost, "in material and power", null));
        }

        return stats;
    }

    public static IReadOnlyList<(string Label, string Value)> Highlights(MonthlyRecapModel m)
    {
        var rows = new List<(string, string)>();
        if (m.MostUsedPrinter is { } printer) rows.Add(("Busiest printer", printer));
        if (m.MostUsedMaterial is { } material) rows.Add(("Most-used material", material));
        if (m.LongestPrint is { } longest) rows.Add(("Longest print", longest));
        return rows;
    }

    public static string Text(MonthlyRecapModel m, EmailFooterModel footer)
    {
        var b = new StringBuilder();
        b.Append(EmailFormat.Greeting(m.Name)).Append("\n\n");
        b.Append(Intro(m)).Append("\n\n");

        foreach (var (value, label, change) in Stats(m))
        {
            b.Append("- ").Append(value).Append(' ').Append(label);
            if (change is not null)
            {
                b.Append(" (").Append(change).Append(')');
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
                b.Append("- ").Append(badge).Append('\n');
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
