using System.Text.Json.Nodes;
using PrintLogApi.Models;

namespace PrintLogApi.Email.Campaigns;

/// <summary>
/// One kind of email. The evaluator asks it who is due; the dispatcher asks it to render a row
/// once every eligibility gate has passed. Each is switched on by
/// <c>Email:Campaigns:&lt;Name&gt;:Enabled</c> (default off).
/// </summary>
public interface IEmailCampaign
{
    /// <summary>Stable id, also the SES tag and utm_campaign, e.g. "monthly-recap".</summary>
    string Name { get; }

    /// <summary>The <see cref="EmailSettingTypes"/> preference that turns this campaign off.</summary>
    int PreferenceSettingTypeId { get; }

    /// <summary>Whether a send of this campaign defers later capped sends (true for all Phase 1 campaigns).</summary>
    bool CountsTowardFrequencyCap { get; }

    /// <summary>Whether this campaign ignores the cap itself (true only for onboarding, whose steps are spaced deliberately).</summary>
    bool ExemptFromFrequencyCap { get; }

    /// <summary>How long a queued row stays sendable: <c>ExpiresAt = SendAfter + Lifetime</c>.</summary>
    TimeSpan Lifetime { get; }

    Task<IReadOnlyList<DueEmail>> FindDueAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Builds the email for a row; null when it is no longer relevant (→ Skipped(not-relevant)).</summary>
    Task<RenderedEmail?> RenderAsync(EmailOutbox row, CancellationToken ct);
}

/// <param name="PeriodKey">What makes this send unique for the user, e.g. "2026-11" or "silent:2026-10-02".</param>
public sealed record DueEmail(long UserId, string PeriodKey, DateTimeOffset SendAfter);

/// <param name="UnsubscribeCategory">The <see cref="EmailSettingTypes"/> id the footer and one-click header switch off.</param>
/// <param name="Exposure">What the email is about, persisted to <see cref="EmailOutbox.Exposure"/> for impact analysis.</param>
public sealed record RenderedEmail(string Subject, string Html, string Text, int UnsubscribeCategory, JsonObject Exposure);
