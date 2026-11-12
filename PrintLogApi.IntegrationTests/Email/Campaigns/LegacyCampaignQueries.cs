using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email;
using PrintLogApi.Email.Campaigns;

namespace PrintLogApi.IntegrationTests.Email.Campaigns;

/// <summary>
/// The due queries of <see cref="MonthlyRecapCampaign"/> and <see cref="PrinterSilentCampaign"/>
/// exactly as they were before they were narrowed: every tick ran the month-wide
/// recap queries and loaded 111 days of automated prints. Kept verbatim as the oracle the
/// narrowed queries must agree with, user for user and tick for tick.
/// </summary>
internal static class LegacyCampaignQueries
{
    private static readonly TimeSpan ZonePadding = TimeSpan.FromHours(14);
    private static readonly TimeSpan RecapLifetime = TimeSpan.FromDays(5);

    private static readonly TimeSpan QuietAtLeast = TimeSpan.FromDays(14);
    private static readonly TimeSpan QuietAtMost = TimeSpan.FromDays(21);
    private static readonly TimeSpan History = TimeSpan.FromDays(90);
    private static readonly TimeSpan RepeatGuard = TimeSpan.FromDays(30);
    private const int MinimumHistory = 3;

    public static async Task<List<DueEmail>> MonthlyRecapDueAsync(PrintLogContext db, EmailOptions settings, DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.UtcDateTime;
        var months = new[] { -1, 0, 1 }
            .Select(d => DateOnly.FromDateTime(nowUtc.AddDays(d)))
            .Select(d => new DateOnly(d.Year, d.Month, 1).AddMonths(-1))
            .Distinct()
            .ToList();

        var due = new List<DueEmail>();
        foreach (var month in months)
        {
            var candidates = await UsersWithPrintsAsync(db, month, ct);
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

                var from = EmailTimeZones.AtLocal(month, 0, zone);
                var to = EmailTimeZones.AtLocal(month.AddMonths(1), 0, zone);
                if (!printed(from, to))
                {
                    continue;
                }

                var sendAfter = EmailTimeZones.AtLocal(thisMonth, settings.LocalSendHour, zone);
                if (sendAfter <= now + settings.EvaluatorInterval && sendAfter + RecapLifetime > now)
                {
                    due.Add(new DueEmail(userId, month.ToString("yyyy-MM", CultureInfo.InvariantCulture), sendAfter));
                }
            }
        }

        return due;
    }

    private static async Task<Dictionary<long, Func<DateTimeOffset, DateTimeOffset, bool>>> UsersWithPrintsAsync(PrintLogContext db, DateOnly month, CancellationToken ct)
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

    public static async Task<List<DueEmail>> PrinterSilentDueAsync(PrintLogContext db, EmailOptions settings, DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.UtcDateTime;
        var rows = await PrinterSilentCampaign.AutomatedPrints(db, nowUtc - QuietAtMost - History).ToListAsync(ct);

        var silentByUser = rows
            .GroupBy(r => (r.UserId, r.PrinterId))
            .Select(g =>
            {
                var last = g.MaxBy(r => r.CreatedDate)!;
                var history = g.Count(r => r.CreatedDate >= last.CreatedDate - History && r.CreatedDate <= last.CreatedDate);
                return (g.Key.UserId, Last: last.CreatedDate, History: history);
            })
            .Where(x => x.Last >= nowUtc - QuietAtMost && x.Last <= nowUtc - QuietAtLeast && x.History >= MinimumHistory)
            .GroupBy(s => s.UserId)
            .ToDictionary(g => g.Key, g => g.Max(s => s.Last));
        if (silentByUser.Count == 0)
        {
            return [];
        }

        var userIds = silentByUser.Keys.ToList();
        var guardSince = now - RepeatGuard;
        var recentlyAlerted = await db.EmailOutbox.AsNoTracking()
            .Where(o => o.Campaign == PrinterSilentCampaign.CampaignName && userIds.Contains(o.UserId) && o.CreatedAt >= guardSince)
            .Select(o => o.UserId)
            .Distinct()
            .ToListAsync(ct);

        var zones = await EmailUserZones.LoadAsync(db, userIds, ct);
        return silentByUser
            .Where(kv => !recentlyAlerted.Contains(kv.Key))
            .Select(kv => new DueEmail(
                kv.Key,
                "silent:" + kv.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                EmailTimeZones.NextLocalHour(now, settings.LocalSendHour, EmailTimeZones.For(zones.GetValueOrDefault(kv.Key), settings.DefaultTimeZone))))
            .ToList();
    }
}
