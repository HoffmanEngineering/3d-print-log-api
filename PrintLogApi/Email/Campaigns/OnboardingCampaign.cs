using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Achievements;
using PrintLogApi.Email.Templates;
using PrintLogApi.Models;

namespace PrintLogApi.Email.Campaigns;

/// <summary>
/// Four emails over a new user's first ten days (spec §7.1). Each step is scheduled from the
/// signup time and rendered only if it still applies: "connect an integration" is dropped once a
/// print arrives automatically, "log your first print" once any print exists.
/// </summary>
public sealed class OnboardingCampaign(
    PrintLogContext db,
    IAchievementQueryService achievements,
    IEmailTemplateRenderer renderer,
    IEmailFooterFactory footers,
    EmailLinkBuilder links,
    IOptions<EmailOptions> options) : IEmailCampaign
{
    public const string CampaignName = "onboarding";
    private const string FooterReason = "You're receiving this because you recently created a 3D Print Log account.";
    private static readonly TimeSpan Window = TimeSpan.FromDays(14);

    /// <summary>The button label for each Getting started hint; anything else falls back to the badges page.</summary>
    private static readonly IReadOnlyDictionary<string, string> CtaLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["first-printer"] = "Add your printer",
        ["first-material"] = "Add a spool",
        ["first-print"] = "Log a print",
        ["plugged-in"] = "Connect your slicer",
        ["first-photo"] = "Add a photo",
        ["maker-profile"] = "Finish your profile",
    };

    public string Name => CampaignName;

    public int PreferenceSettingTypeId => EmailSettingTypes.Onboarding;

    public bool CountsTowardFrequencyCap => true;

    public bool ExemptFromFrequencyCap => true;

    public TimeSpan Lifetime => TimeSpan.FromDays(2);

    public async Task<IReadOnlyList<DueEmail>> FindDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var settings = options.Value;
        if (settings.Onboarding.StartDate is not { } startDate)
        {
            return [];
        }

        var earliest = now - Window > startDate ? now - Window : startDate;
        var users = await db.Users
            .AsNoTracking()
            .Where(u => u.CreatedDate != null && u.CreatedDate >= earliest && u.CreatedDate <= now)
            .Select(u => new { u.Id, CreatedDate = u.CreatedDate!.Value })
            .ToListAsync(ct);

        var zones = await EmailUserZones.LoadAsync(db, users.Select(u => u.Id).ToList(), ct);
        var horizon = now + settings.EvaluatorInterval;
        var due = new List<DueEmail>();

        foreach (var user in users)
        {
            var zone = EmailTimeZones.For(zones.GetValueOrDefault(user.Id), settings.DefaultTimeZone);
            var signupDay = EmailTimeZones.LocalDate(user.CreatedDate, zone);
            DateTimeOffset DayAt(int day) => EmailTimeZones.AtLocal(signupDay.AddDays(day), settings.LocalSendHour, zone);

            (string Step, DateTimeOffset SendAfter)[] schedule =
            [
                (OnboardingTemplates.Welcome, user.CreatedDate.AddMinutes(15)),
                (OnboardingTemplates.Connect, DayAt(2)),
                (OnboardingTemplates.FirstPrint, DayAt(5)),
                (OnboardingTemplates.NextSteps, DayAt(10)),
            ];

            // Not yet due, or already past its whole lifetime (it would only be queued to expire).
            due.AddRange(schedule
                .Where(s => s.SendAfter <= horizon && s.SendAfter + Lifetime > now)
                .Select(s => new DueEmail(user.Id, s.Step, s.SendAfter)));
        }

        return due;
    }

    public async Task<RenderedEmail?> RenderAsync(EmailOutbox row, CancellationToken ct)
    {
        if (!OnboardingTemplates.Steps.Contains(row.PeriodKey))
        {
            return null;
        }

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == row.UserId)
            .Select(u => new { u.DisplayName })
            .SingleOrDefaultAsync(ct);
        if (user is null)
        {
            return null;
        }

        var prints = db.Prints.AsNoTracking().Where(p => p.CreatedById == row.UserId);
        var printCount = await prints.CountAsync(ct);

        var relevant = row.PeriodKey switch
        {
            OnboardingTemplates.Connect => !await prints.AnyAsync(p => AutomatedPrintSources.All.Contains(p.Source), ct),
            OnboardingTemplates.FirstPrint => printCount == 0,
            OnboardingTemplates.NextSteps => printCount > 0,
            _ => true,
        };
        if (!relevant)
        {
            return null;
        }

        var model = await BuildModelAsync(row, user.DisplayName, printCount, prints, ct);
        var (footer, _) = footers.Create(row.UserId, PreferenceSettingTypeId, FooterReason);
        var (html, text) = await OnboardingTemplates.RenderAsync(renderer, model, footer);

        return new RenderedEmail(
            OnboardingTemplates.Subject(row.PeriodKey), html, text, PreferenceSettingTypeId,
            new JsonObject { ["step"] = row.PeriodKey });
    }

    private async Task<OnboardingModel> BuildModelAsync(EmailOutbox row, string? name, int printCount, IQueryable<Print> prints, CancellationToken ct)
    {
        string Link(string path) => links.Web(path, CampaignName, row.PeriodKey);

        // Hints only matter on the steps that show them; GetMineAsync runs a reconciliation pass.
        var hintRoute = "/printers/new";
        var hintLabel = CtaLabels["first-printer"];
        var hintText = "Add your first printer.";
        int held = 0, total = 0;
        if (row.PeriodKey is OnboardingTemplates.Welcome or OnboardingTemplates.NextSteps)
        {
            var mine = await achievements.GetMineAsync(row.UserId, ct);
            if (mine.NextHint is { } hint && AchievementCatalog.Find(hint.Key) is { } definition)
            {
                hintRoute = hint.CtaRoute;
                hintLabel = CtaLabels.GetValueOrDefault(hint.Key, "See your badges");
                hintText = AchievementCopy.Format(definition.DescriptionTemplate, definition.Thresholds[Math.Clamp(hint.Tier, 1, definition.Thresholds.Count) - 1]);
            }

            var gettingStarted = AchievementCatalog.Definitions
                .Where(d => d.Category == AchievementCategory.GettingStarted && !d.Retired)
                .Select(d => d.Key)
                .ToHashSet(StringComparer.Ordinal);
            total = gettingStarted.Count;
            held = mine.Families.Count(f => gettingStarted.Contains(f.Key) && f.Tiers.Count > 0);
        }

        double hours = 0, grams = 0;
        if (row.PeriodKey == OnboardingTemplates.NextSteps)
        {
            hours = (await prints.SumAsync(p => (long?)p.PrintTimeInSeconds, ct) ?? 0) / 3600.0;
            grams = (await prints.SelectMany(p => p.FilamentUsage!).SumAsync(f => (long?)f.AmountMg, ct) ?? 0) / 1000.0;
        }

        return new OnboardingModel(
            row.PeriodKey,
            name,
            Link(hintRoute),
            hintLabel,
            hintText,
            printCount,
            hours,
            grams,
            held,
            total,
            Link("/docs/octoprint-webhook"),
            Link("/docs/klipper"),
            Link("/docs/slic3r-uploader"),
            Link("/prints/new/edit"));
    }
}
