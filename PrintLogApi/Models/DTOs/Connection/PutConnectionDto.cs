using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Connection;

/// <summary>
/// What an agent sends when it starts and on every heartbeat. Each call replaces these fields, so
/// send the printer binding every time: leaving it out unbinds the connection.
/// </summary>
public class PutConnectionDto : IValidatableObject
{
    /// <summary>The connector type, e.g. <c>moonraker</c>. Stored lower-case.</summary>
    [StringLength(50)]
    public string? Kind { get; set; }

    /// <summary>The name shown on prints it logs: "Logged automatically by …".</summary>
    [StringLength(100)]
    public string? DisplayName { get; set; }

    [StringLength(50)]
    public string? AgentVersion { get; set; }

    /// <summary>The printer the user chose at pairing. Must be one of yours.</summary>
    public long? PrinterId { get; set; }

    // The agent's health (printlog-bridge#37), all optional. A value too long is cut and a
    // negative number counts as 0: health is never a reason to refuse the heartbeat itself.

    /// <summary>How the agent reaches the printer: <c>local</c> or <c>remote</c>. At most 20 characters.</summary>
    public string? Mode { get; set; }

    /// <summary>Klipper's version, at most 50 characters.</summary>
    public string? KlipperVersion { get; set; }

    /// <summary>Moonraker's version, at most 50 characters.</summary>
    public string? MoonrakerVersion { get; set; }

    /// <summary>The printer model the slicer wrote in the last job's file. At most 100 characters.</summary>
    public string? PrinterModel { get; set; }

    /// <summary>Events and pictures waiting in the agent's outbox to be sent.</summary>
    public int? OutboxDepth { get; set; }

    /// <summary>How long the oldest of them has waited, in seconds.</summary>
    public long? OutboxOldestAgeSeconds { get; set; }

    /// <summary>
    /// The last warning or error the agent logged for this printer, at most 500 characters. The
    /// agent removes host names, IP addresses, file names and keys before sending it.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>When it was logged. A time in the future is taken as now.</summary>
    public DateTimeOffset? LastErrorAt { get; set; }

    /// <summary>Warnings and errors logged since the agent's previous heartbeat that got through.</summary>
    public int? ErrorCount { get; set; }

    /// <summary>Whether a webcam frame is added to each print.</summary>
    public bool? Snapshots { get; set; }

    /// <summary>Whether each print's slicer settings are sent.</summary>
    public bool? SlicerSettings { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Kind))
        {
            yield return new ValidationResult("kind is required.", [nameof(Kind)]);
        }
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            yield return new ValidationResult("displayName is required.", [nameof(DisplayName)]);
        }
    }
}
