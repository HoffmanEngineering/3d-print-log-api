using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

public enum EmailOutboxStatus : byte
{
    Pending = 0,
    Sending = 1,
    Sent = 2,
    Failed = 3,
    Skipped = 4,
}

/// <summary>
/// One email a campaign decided a user is due, and everything that happened to it. The unique
/// (UserId, Campaign, PeriodKey) index is what makes a duplicate send impossible: an evaluator that
/// runs twice, or a restart halfway through a tick, cannot queue the same email a second time.
/// Content is rendered at send time from current data, never stored here.
/// </summary>
public class EmailOutbox
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public long UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Campaign name, e.g. <c>monthly-recap</c>.</summary>
    [MaxLength(64)]
    public string Campaign { get; set; } = null!;

    /// <summary>What makes this email distinct within the campaign: <c>2026-11</c>, <c>welcome</c>, <c>silent:2026-10-02</c>.</summary>
    [MaxLength(128)]
    public string PeriodKey { get; set; } = null!;

    public EmailOutboxStatus Status { get; set; }

    /// <summary>Why a row ended <see cref="EmailOutboxStatus.Skipped"/>; see <c>EmailSkipReasons</c>.</summary>
    [MaxLength(32)]
    public string? SkipReason { get; set; }

    public int Attempts { get; set; }

    /// <summary>Earliest moment the email may go out (usually 9am in the user's zone).</summary>
    public DateTimeOffset SendAfter { get; set; }

    /// <summary>When the dispatcher should next look at the row; moved by retries and deferrals.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>After this, a still-pending row is skipped as expired.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the row was claimed for sending; used to detect a process that died mid-send.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    [MaxLength(128)]
    public string? ProviderMessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    [MaxLength(512)]
    public string? LastError { get; set; }

    /// <summary>
    /// JSON snapshot of what the email was about (e.g. which printers and their last report), written
    /// when the row passes eligibility, for treatment and holdout alike. Immutable once written.
    /// </summary>
    public string? Exposure { get; set; }

    /// <summary>Keyed hash of the address the message went to, so a complaint whose recipient the mailbox provider redacted can still be suppressed.</summary>
    [MaxLength(64)]
    public string? SentTo { get; set; }
}
