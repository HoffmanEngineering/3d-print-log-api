using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Email.Templates;
using PrintLogApi.Models;

namespace PrintLogApi.Email.Campaigns;

/// <summary>
/// "Your printer went quiet": a printer that reported prints automatically and regularly has sent
/// nothing for two to three weeks (spec §7.2). Usually a broken integration, not a broken habit.
///
/// "Print date" here is <see cref="TimestampEntity.CreatedDate"/>, the moment the integration
/// reported the print, compared as a UTC <see cref="DateTime"/>.
/// </summary>
public sealed class PrinterSilentCampaign(
    PrintLogContext db,
    IEmailTemplateRenderer renderer,
    IEmailFooterFactory footers,
    EmailLinkBuilder links,
    IOptions<EmailOptions> options) : IEmailCampaign
{
    public const string CampaignName = "printer-silent";
    private const string KeyPrefix = "silent:";
    private const string FooterReason = "You're receiving this because printer alerts are on for your 3D Print Log account.";

    private static readonly TimeSpan QuietAtLeast = TimeSpan.FromDays(14);
    private static readonly TimeSpan QuietAtMost = TimeSpan.FromDays(21);
    private static readonly TimeSpan History = TimeSpan.FromDays(90);
    private static readonly TimeSpan RepeatGuard = TimeSpan.FromDays(30);
    private const int MinimumHistory = 3;

    public string Name => CampaignName;

    public int PreferenceSettingTypeId => EmailSettingTypes.PrinterSilent;

    public bool CountsTowardFrequencyCap => true;

    public bool ExemptFromFrequencyCap => false;

    public TimeSpan Lifetime => TimeSpan.FromDays(3);

    public sealed record AutomatedPrint(long UserId, long PrinterId, DateTime CreatedDate, PrintSource Source);

    /// <summary>
    /// Stage 1: automated prints of active printers since <paramref name="sinceUtc"/>, as plain rows.
    /// The max / count per printer is done in memory (stage 2): an aggregate inside an aggregate
    /// predicate is what SQL Server rejects with error 8124, and SQLite would never tell us.
    /// </summary>
    internal static IQueryable<AutomatedPrint> AutomatedPrints(PrintLogContext db, DateTime sinceUtc, long? userId = null)
    {
        var prints = db.Prints.AsNoTracking()
            .Where(p => p.CreatedDate >= sinceUtc && AutomatedPrintSources.All.Contains(p.Source) && p.Printer.IsActive);
        if (userId is { } id)
        {
            prints = prints.Where(p => p.Printer.UserId == id);
        }

        return prints.Select(p => new AutomatedPrint(p.Printer.UserId, p.PrinterId, p.CreatedDate, p.Source));
    }

    public async Task<IReadOnlyList<DueEmail>> FindDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.UtcDateTime;
        var rows = await AutomatedPrints(db, nowUtc - QuietAtMost - History).ToListAsync(ct);

        var silentByUser = Silent(rows, last => last >= nowUtc - QuietAtMost && last <= nowUtc - QuietAtLeast)
            .GroupBy(s => s.UserId)
            .ToDictionary(g => g.Key, g => g.Max(s => s.Last));
        if (silentByUser.Count == 0)
        {
            return [];
        }

        var userIds = silentByUser.Keys.ToList();
        var guardSince = now - RepeatGuard;
        var recentlyAlerted = await db.EmailOutbox.AsNoTracking()
            .Where(o => o.Campaign == CampaignName && userIds.Contains(o.UserId) && o.CreatedAt >= guardSince)
            .Select(o => o.UserId)
            .Distinct()
            .ToListAsync(ct);

        var zones = await EmailUserZones.LoadAsync(db, userIds, ct);
        var settings = options.Value;

        return silentByUser
            .Where(kv => !recentlyAlerted.Contains(kv.Key))
            .Select(kv => new DueEmail(
                kv.Key,
                KeyPrefix + kv.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                EmailTimeZones.NextLocalHour(now, settings.LocalSendHour, EmailTimeZones.For(zones.GetValueOrDefault(kv.Key), settings.DefaultTimeZone))))
            .ToList();
    }

    public async Task<RenderedEmail?> RenderAsync(EmailOutbox row, CancellationToken ct)
    {
        if (!row.PeriodKey.StartsWith(KeyPrefix, StringComparison.Ordinal)
            || !DateOnly.TryParseExact(row.PeriodKey[KeyPrefix.Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var keyDay))
        {
            return null;
        }

        // The printers this email was about: their last automated print fell on or before the
        // key day and within the same 7-day window. One that has reported since is past the key
        // day and drops out, which is the render-time recheck.
        var keyStart = keyDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var keyEnd = keyStart.AddDays(1);
        var windowStart = keyStart - (QuietAtMost - QuietAtLeast);
        var rows = await AutomatedPrints(db, windowStart - History, row.UserId).ToListAsync(ct);

        var silent = Silent(rows, last => last >= windowStart && last < keyEnd).ToList();
        if (silent.Count == 0)
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
        string Day(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("MMM d", CultureInfo.InvariantCulture);
        string Link(string path) => links.Web(path, CampaignName, row.PeriodKey);

        var printerIds = silent.Select(s => s.PrinterId).ToList();
        var printers = await db.Printers.AsNoTracking()
            .Where(p => printerIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Make, p.Model })
            .ToDictionaryAsync(p => p.Id, ct);

        var views = silent
            .OrderByDescending(s => s.Last)
            .Where(s => printers.ContainsKey(s.PrinterId))
            .Select(s =>
            {
                var p = printers[s.PrinterId];
                var makeModel = $"{p.Make} {p.Model}".Trim();
                var name = string.IsNullOrWhiteSpace(p.Name) ? (makeModel.Length > 0 ? makeModel : "printer") : p.Name.Trim();
                var (docPath, docLabel) = Troubleshooting(s.Source);
                return new SilentPrinterView(
                    name,
                    string.IsNullOrWhiteSpace(p.Name) || makeModel.Length == 0 ? null : makeModel,
                    SourceLabel(s.Source),
                    Day(s.Last),
                    Link(docPath),
                    docLabel,
                    Link($"/printers/{s.PrinterId}"));
            })
            .ToList();

        // Rotated or revoked keys are the most common reason an API-key integration stops.
        var newestKeyUse = await db.UserApiKeys.AsNoTracking()
            .Where(k => k.UserId == row.UserId && !k.IsDeleted && k.LastUsed != null)
            .OrderByDescending(k => k.LastUsed)
            .Select(k => k.LastUsed)
            .FirstOrDefaultAsync(ct);
        var latestSilence = silent.Max(s => s.Last);
        var apiKeyNote = newestKeyUse is { } used && used.UtcDateTime < latestSilence ? Day(used.UtcDateTime) : null;

        var model = new PrinterSilentModel(user.DisplayName, views, apiKeyNote);
        var (footer, _) = footers.Create(row.UserId, PreferenceSettingTypeId, FooterReason);
        var (html, text) = await PrinterSilentTemplates.RenderAsync(renderer, model, footer);

        var exposure = new JsonObject
        {
            ["printers"] = new JsonArray([.. silent.Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.PrinterId,
                ["lastAutomatedPrintUtc"] = DateTime.SpecifyKind(s.Last, DateTimeKind.Utc),
                ["source"] = s.Source.ToString(),
            })]),
        };

        return new RenderedEmail(PrinterSilentTemplates.Subject(model), html, text, PreferenceSettingTypeId, exposure);
    }

    private sealed record SilentPrinter(long UserId, long PrinterId, DateTime Last, PrintSource Source);

    /// <summary>Stage 2: per printer, its last automated print L, kept when L passes the window test and at least three automated prints fall in [L − 90d, L].</summary>
    private static IEnumerable<SilentPrinter> Silent(IEnumerable<AutomatedPrint> rows, Func<DateTime, bool> inWindow)
        => rows
            .GroupBy(r => (r.UserId, r.PrinterId))
            .Select(g =>
            {
                var last = g.MaxBy(r => r.CreatedDate)!;
                var history = g.Count(r => r.CreatedDate >= last.CreatedDate - History && r.CreatedDate <= last.CreatedDate);
                return (Printer: new SilentPrinter(g.Key.UserId, g.Key.PrinterId, last.CreatedDate, last.Source), History: history);
            })
            .Where(x => inWindow(x.Printer.Last) && x.History >= MinimumHistory)
            .Select(x => x.Printer);

    private static string SourceLabel(PrintSource source) => source switch
    {
        PrintSource.Moonraker => "Moonraker",
        PrintSource.OctoPrint => "OctoPrint",
        PrintSource.SlicerPlugin => "the slicer uploader",
        PrintSource.ApiKey => "an API key",
        _ => source.ToString(),
    };

    private static (string Path, string Label) Troubleshooting(PrintSource source) => source switch
    {
        PrintSource.Moonraker => ("/docs/klipper", "Klipper setup guide"),
        PrintSource.OctoPrint => ("/docs/octoprint-webhook", "OctoPrint setup guide"),
        PrintSource.SlicerPlugin => ("/docs/slic3r-uploader", "Slicer uploader guide"),
        _ => ("/api-keys", "Your API keys"),
    };
}
