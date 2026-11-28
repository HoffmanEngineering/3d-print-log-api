using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>What a timeline entry records. Stored as an int: append, never renumber.</summary>
public enum PrintTimelineEventType
{
    Started = 1,
    Finished = 2,
    Claimed = 3,

    /// <summary>A span: <see cref="PrintTimelineEvent.EndedAt"/> is set when the print resumes.</summary>
    Paused = 10,
    Failure = 11,
}

/// <summary>
/// One entry in a print's timeline (api#343): a pause, the failure reason, a lifecycle step. Only
/// explains the print: status, dates, duration and usage stay on <see cref="Print"/> and never
/// follow from these rows. "Timeline" because <c>PrintEventService</c> already names the notifier's
/// start and finish pipeline. Rules live in <c>PrintTimeline</c>.
/// </summary>
public class PrintTimelineEvent
{
    public const int MaxKeyLength = 200;
    public const int MaxCodeLength = 64;
    public const int MaxMessageLength = 500;

    /// <summary>The most entries one print keeps; later ones are dropped.</summary>
    public const int MaxPerPrint = 200;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public long PrintId { get; set; }
    public Print Print { get; set; } = null!;

    public PrintTimelineEventType Type { get; set; }

    /// <summary>The printer's or bridge's time when a connector sent it; the server's otherwise.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>When a span ended (a pause's resume). Null while it is open, and on point events.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>What recorded it.</summary>
    public PrintSource Source { get; set; }

    /// <summary>A short machine code: the finished status, a printer's error code.</summary>
    [MaxLength(MaxCodeLength)]
    public string? Code { get; set; }

    /// <summary>Text for people: the failure reason.</summary>
    [MaxLength(MaxMessageLength)]
    public string? Message { get; set; }

    /// <summary>Unique per print, so a replayed event is recognised and ignored.</summary>
    [Required]
    [MaxLength(MaxKeyLength)]
    public string Key { get; set; } = null!;
}
