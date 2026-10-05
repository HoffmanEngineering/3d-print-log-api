using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Achievements;
using PrintLogApi.Email.Templates;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services.Analytics;

namespace PrintLogApi.Email.Campaigns;

/// <summary>
/// "Your November in prints": the previous calendar month, in the user's own zone, sent at
/// <see cref="EmailOptions.LocalSendHour"/> on the 1st (spec §7.3). Only users who logged at
/// least one print that month get one.
///
/// The month is measured on <c>Print.StartDate</c> by the analytics overview service, the same
/// numbers the analytics page shows for that month.
/// </summary>
public sealed class MonthlyRecapCampaign(
    PrintLogContext db,
    IAnalyticsService analytics,
    IEmailTemplateRenderer renderer,
    IEmailFooterFactory footers,
    EmailLinkBuilder links,
    IOptions<EmailOptions> options) : IEmailCampaign
{
    public const string CampaignName = "monthly-recap";
    private const string KeyFormat = "yyyy-MM";
    private const string FooterReason = "You're receiving this because monthly recaps are on for your 3D Print Log account.";

    /// <summary>Wider than any zone's offset from UTC (+14h / -12h), so a UTC window padded by it holds every zone's local month.</summary>
    private static readonly TimeSpan ZonePadding = TimeSpan.FromHours(14);

    public string Name => CampaignName;

    public int PreferenceSettingTypeId => EmailSettingTypes.MonthlyRecap;

    public bool CountsTowardFrequencyCap => true;

    public bool ExemptFromFrequencyCap => false;

    public TimeSpan Lifetime => TimeSpan.FromDays(5);

    public async Task<IReadOnlyList<DueEmail>> FindDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var settings = options.Value;
        var nowUtc = now.UtcDateTime;

        // Somewhere in the world it is already tomorrow or still yesterday, so the month that
        // just ended differs by zone. Every candidate is "the month before" one of these dates.
        var months = new[] { -1, 0, 1 }
            .Select(d => DateOnly.FromDateTime(nowUtc.AddDays(d)))
            .Select(d => new DateOnly(d.Year, d.Month, 1).AddMonths(-1))
            .Distinct()
            .ToList();

        var due = new List<DueEmail>();
        foreach (var month in months)
        {
            var candidates = await UsersWithPrintsAsync(month, ct);
            if (candidates.Count == 0)
            {
                continue;
            }

            var zones = await EmailUserZones.LoadAsync(db, candidates.Keys, ct);
            foreach (var (userId, printed) in candidates)
            {
                var zone = EmailTimeZones.For(zones.GetValueOrDefault(userId), settings.DefaultTimeZone);
                var localToday = EmailTimeZones.LocalDate(now, zone);
                var thisMonth = new DateOnly(localToday.Year, localToday.Month, 1);
                if (thisMonth.AddMonths(-1) != month)
                {
                    continue;
                }

                var (from, to) = LocalMonth(month, zone);
                if (!printed(from, to))
                {
                    continue;
                }

                var sendAfter = EmailTimeZones.AtLocal(thisMonth, settings.LocalSendHour, zone);
                if (sendAfter <= now + settings.EvaluatorInterval && sendAfter + Lifetime > now)
                {
                    due.Add(new DueEmail(userId, month.ToString(KeyFormat, CultureInfo.InvariantCulture), sendAfter));
                }
            }
        }

        return due;
    }

    /// <summary>
    /// Users with a print in <paramref name="month"/> for some zone, each with a test for "a print
    /// in this exact local window". Prints well inside the month count for every zone, so those
    /// users come back as a plain distinct list; only the two padded edges need their dates.
    /// </summary>
    private async Task<Dictionary<long, Func<DateTimeOffset, DateTimeOffset, bool>>> UsersWithPrintsAsync(DateOnly month, CancellationToken ct)
    {
        var start = new DateTimeOffset(month.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = start.AddMonths(1);
        var coreStart = start + ZonePadding;
        var coreEnd = end - ZonePadding;
        var outerStart = start - ZonePadding;
        var outerEnd = end + ZonePadding;

        var core = await db.Prints.AsNoTracking()
            .Where(p => p.StartDate >= coreStart && p.StartDate < coreEnd)
            .Select(p => p.CreatedById)
            .Distinct()
            .ToListAsync(ct);

        var edges = await db.Prints.AsNoTracking()
            .Where(p => (p.StartDate >= outerStart && p.StartDate < coreStart) || (p.StartDate >= coreEnd && p.StartDate < outerEnd))
            .Select(p => new { p.CreatedById, p.StartDate })
            .ToListAsync(ct);

        var result = new Dictionary<long, Func<DateTimeOffset, DateTimeOffset, bool>>();
        foreach (var userId in core)
        {
            result[userId] = static (_, _) => true;
        }

        foreach (var group in edges.GroupBy(e => e.CreatedById).Where(g => !result.ContainsKey(g.Key)))
        {
            var dates = group.Select(e => e.StartDate!.Value).ToList();
            result[group.Key] = (from, to) => dates.Exists(d => d >= from && d < to);
        }

        return result;
    }

    private static (DateTimeOffset From, DateTimeOffset To) LocalMonth(DateOnly month, TimeZoneInfo zone)
        => (EmailTimeZones.AtLocal(month, 0, zone), EmailTimeZones.AtLocal(month.AddMonths(1), 0, zone));

    public async Task<RenderedEmail?> RenderAsync(EmailOutbox row, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(row.PeriodKey + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return null;
        }

        var user = await db.Users.AsNoTracking().Where(u => u.Id == row.UserId).Select(u => new { u.DisplayName }).SingleOrDefaultAsync(ct);
        if (user is null)
        {
            return null;
        }

        var settings = options.Value;
        var zone = EmailTimeZones.For(await EmailUserZones.LoadAsync(db, row.UserId, ct), settings.DefaultTimeZone);

        // Two plain month queries rather than ComparePrevious: the overview's previous window is
        // the same number of days, which for a month is not the calendar month before it.
        var current = await OverviewAsync(row.UserId, month, zone, ct);
        var printCount = (int)Math.Round(current.Tiles.PrintCount.Value ?? 0);
        if (printCount == 0)
        {
            return null;
        }

        var previous = await OverviewAsync(row.UserId, month.AddMonths(-1), zone, ct);
        var tiles = current.Tiles;
        var hours = (tiles.PrintTimeSeconds.Value ?? 0) / 3600;
        var previousCount = previous.Tiles.PrintCount.Value ?? 0;
        var previousHours = (previous.Tiles.PrintTimeSeconds.Value ?? 0) / 3600;

        var (monthStartUtc, monthEndUtc) = LocalMonth(month, zone);
        var startUtc = monthStartUtc.UtcDateTime;
        var endUtc = monthEndUtc.UtcDateTime;
        var badges = (await db.UserAchievements.AsNoTracking()
                .Where(a => a.UserId == row.UserId && a.UnlockedAt >= startUtc && a.UnlockedAt < endUtc)
                .OrderBy(a => a.UnlockedAt)
                .Select(a => new { a.AchievementKey, a.Tier })
                .ToListAsync(ct))
            .Select(a => (Definition: AchievementCatalog.Find(a.AchievementKey), a.Tier))
            .Where(a => a.Definition is not null && a.Tier >= 1 && a.Tier <= AchievementCatalog.TierNames.Count)
            .Select(a => $"{a.Definition!.Title} ({AchievementCatalog.TierNames[a.Tier - 1]})")
            .ToList();

        string Link(string path) => links.Web(path, CampaignName, row.PeriodKey);
        var highlights = current.Highlights;

        var model = new MonthlyRecapModel(
            Name: user.DisplayName,
            MonthName: MonthName(month),
            PreviousMonthName: MonthName(month.AddMonths(-1)),
            PrintCount: printCount,
            SuccessRatePercent: tiles.SuccessRatePercent.Value,
            PrintHours: hours,
            FilamentGrams: tiles.FilamentGrams.Value ?? 0,
            Cost: tiles.TotalCost.Value is { } cost and > 0
                ? $"{cost.ToString("N2", CultureInfo.InvariantCulture)} {tiles.TotalCost.Currency}".Trim()
                : null,
            PrintCountChangePercent: ChangePercent(printCount, previousCount),
            PrintHoursChangePercent: ChangePercent(hours, previousHours),
            MostUsedPrinter: Blank(highlights.MostUsedPrinter?.Label),
            MostUsedMaterial: Blank(highlights.MostUsedMaterial?.Label),
            LongestPrint: Longest(highlights.LongestPrint),
            Badges: badges,
            Tip: await RecapTips.ForAsync(db, row.UserId, Link, ct),
            StatsUrl: Link("/analytics"));

        var (footer, _) = footers.Create(row.UserId, PreferenceSettingTypeId, FooterReason);
        var (html, text) = await MonthlyRecapTemplates.RenderAsync(renderer, model, footer);
        var exposure = new JsonObject { ["month"] = row.PeriodKey, ["printCount"] = printCount };

        return new RenderedEmail(MonthlyRecapTemplates.Subject(model), html, text, PreferenceSettingTypeId, exposure);
    }

    private Task<OverviewResponse> OverviewAsync(long userId, DateOnly month, TimeZoneInfo zone, CancellationToken ct)
    {
        var (from, to) = LocalMonth(month, zone);
        return analytics.GetOverview(userId, new AnalyticsFilter { FromDate = from, ToDate = to, TimeZone = zone.Id }, ct);
    }

    private static string MonthName(DateOnly month) => month.ToString("MMMM", CultureInfo.InvariantCulture);

    private static int? ChangePercent(double current, double previous)
        => previous > 0 ? (int)Math.Round((current - previous) / previous * 100, MidpointRounding.AwayFromZero) : null;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Longest(HighlightRef? longest)
        => Blank(longest?.Label) is { } title && longest!.Value is { } seconds and > 0
            ? $"{title} ({EmailFormat.Hours(seconds / 3600)})"
            : Blank(longest?.Label);
}
