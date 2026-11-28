using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Timeline;

/// <summary>Pauses and resumes a connector reports for one print (api#343).</summary>
public class PostTimelineDto : IValidatableObject
{
    /// <summary>The most events one request may carry.</summary>
    public const int MaxEvents = 20;

    /// <summary>The event types this API takes. Anything else fails the whole batch.</summary>
    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal) { "pause", "resume" };

    /// <summary>The events, applied in time order.</summary>
    public List<PostTimelineEventDto?>? Events { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var events = Events ?? [];
        if (events.Count == 0)
        {
            yield return new ValidationResult("events must hold at least one event.", [nameof(Events)]);
            yield break;
        }
        if (events.Count > MaxEvents)
        {
            yield return new ValidationResult($"events may hold at most {MaxEvents} events.", [nameof(Events)]);
            yield break;
        }
        foreach (var e in events)
        {
            if (e is null || e.Type is null || !Types.Contains(e.Type))
            {
                yield return new ValidationResult("Each event's type must be pause or resume.", [nameof(Events)]);
                yield break;
            }
            if (e.OccurredAt is null)
            {
                yield return new ValidationResult("Each event needs occurredAt.", [nameof(Events)]);
                yield break;
            }
            if (string.IsNullOrWhiteSpace(e.Key) || e.Key.Length > PrintTimelineEvent.MaxKeyLength)
            {
                yield return new ValidationResult(
                    $"Each event needs a key of at most {PrintTimelineEvent.MaxKeyLength} characters.", [nameof(Events)]);
                yield break;
            }
        }
    }
}

/// <summary>One pause or resume.</summary>
public class PostTimelineEventDto
{
    /// <summary><c>pause</c> or <c>resume</c>.</summary>
    public string? Type { get; set; }

    /// <summary>When the printer paused or resumed, with its offset.</summary>
    public DateTimeOffset? OccurredAt { get; set; }

    /// <summary>
    /// Unique per print and stable across retries, such as <c>instance:job:pause:1764252131</c>: a
    /// pause whose key is already recorded is ignored.
    /// </summary>
    public string? Key { get; set; }
}

/// <summary>A print's timeline, oldest first.</summary>
public class TimelineDto
{
    public List<TimelineEventDto>? Events { get; set; }
}

/// <summary>One timeline entry.</summary>
public class TimelineEventDto
{
    public long Id { get; set; }

    /// <summary><c>started</c>, <c>finished</c>, <c>claimed</c>, <c>paused</c> or <c>failure</c>; more may be added.</summary>
    public string? Type { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>When a pause ended; null while it is open and on other entries.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>What recorded it, such as <c>web</c>, <c>apiKey</c> or <c>moonraker</c>.</summary>
    public string? Source { get; set; }

    /// <summary>The finished status (<c>Success</c>…) or the printer's failure code.</summary>
    public string? Code { get; set; }

    /// <summary>The failure reason in words.</summary>
    public string? Message { get; set; }

    public static TimelineEventDto From(PrintTimelineEvent e) => new()
    {
        Id = e.Id,
        Type = Camel(e.Type.ToString()),
        OccurredAt = e.OccurredAt,
        EndedAt = e.EndedAt,
        Source = Camel(e.Source.ToString()),
        Code = e.Code,
        Message = e.Message,
    };

    private static string Camel(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
