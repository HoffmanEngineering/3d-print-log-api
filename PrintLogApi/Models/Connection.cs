using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// One connector attached to one printer: a bridge watching three printers registers three. The
/// agent upserts it by <see cref="InstanceId"/> at start and on every heartbeat. Deleting it keeps
/// the prints logged through it.
///
/// Not a TimestampEntity, for the same reason as <see cref="DeviceToken"/>: an agent's heartbeat
/// has no meaningful "updated by", and TimestampEntity's audit FKs are required.
/// </summary>
public class Connection
{
    /// <summary>How long without a heartbeat before a connection is stale. Agents beat every 5 minutes.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>The <see cref="Kind"/> the printlog-bridge registers for a Moonraker printer.</summary>
    public const string MoonrakerKind = "moonraker";

    /// <summary>
    /// The <see cref="Kind"/> the printlog-bridge registers for an OctoPrint printer (its adapter's
    /// name, printlog-bridge#83). Its prints carry the external source <c>octoprint</c>.
    /// </summary>
    public const string OctoPrintKind = "octoprint";

    /// <summary>
    /// The kinds printlog-bridge registers, one per adapter (printlog-bridge#82): Moonraker,
    /// OctoPrint, PrusaLink, Bambu Lab, Elegoo's SDCP, Duet (RepRapFirmware), FlashForge and Anycubic
    /// LAN mode. A connection of any of them is a bridge
    /// that logs its printer's prints itself.
    /// </summary>
    public static readonly string[] BridgeKinds = [MoonrakerKind, OctoPrintKind, "prusalink", "bambu", "sdcp", "duet", "flashforge", "anycubic"];

    /// <summary>Whether <paramref name="kind"/> is one of <see cref="BridgeKinds"/>, ignoring case.</summary>
    public static bool IsBridgeKind(string? kind) =>
        kind is not null && BridgeKinds.Contains(kind, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The source a print created through a connection of this kind records, or null to keep the
    /// caller's. <c>moonraker</c> has none on purpose: its bridge prints have always been
    /// <see cref="PrintSource.ApiKey"/>, and <see cref="PrintSource.Moonraker"/> means the
    /// notifier. Moving them would change what existing users' new prints count toward
    /// (achievements, the sync snapshot, the silent-printer email), which needs its own decision.
    /// </summary>
    public static PrintSource? PrintSourceForKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        OctoPrintKind => PrintSource.OctoPrint,
        "prusalink" => PrintSource.PrusaLink,
        "bambu" => PrintSource.Bambu,
        "sdcp" => PrintSource.Sdcp,
        "duet" => PrintSource.Duet,
        "flashforge" => PrintSource.FlashForge,
        "anycubic" => PrintSource.Anycubic,
        _ => null,
    };

    [Key]
    public Guid Id { get; set; }

    public long UserId { get; set; }
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;

    /// <summary>The connector type, lower-case: one of <see cref="BridgeKinds"/> for printlog-bridge.</summary>
    [Required]
    [MaxLength(50)]
    public string Kind { get; set; } = null!;

    /// <summary>The agent's own stable id for this printer, unique per user.</summary>
    [Required]
    [MaxLength(100)]
    public string InstanceId { get; set; } = null!;

    [Required]
    [MaxLength(100)]
    public string DisplayName { get; set; } = null!;

    [MaxLength(50)]
    public string? AgentVersion { get; set; }

    /// <summary>The printer the user bound this connection to at pairing, or null when unbound.</summary>
    public long? PrinterId { get; set; }
    [ForeignKey("PrinterId")]
    public Printer? Printer { get; set; }

    /// <summary>
    /// The paired bridge that sent the last heartbeat (printlog-bridge#103), taken from its device
    /// token, never from the body. Null for an API-key agent, or a bridge that has not checked in.
    /// </summary>
    public long? BridgeId { get; set; }
    [ForeignKey("BridgeId")]
    public Bridge? Bridge { get; set; }

    public DateTime CreatedDate { get; set; }

    /// <summary>The last heartbeat, UTC.</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>
    /// Events dropped because this connection was logging the same printer: Moonraker notifier
    /// events on a <c>moonraker</c> connection (#150), OctoPrint webhook events on an
    /// <c>octoprint</c> one (#291). Non-zero means the user still has a <c>[notifier]</c> section,
    /// or the OctoPrint Webhooks plugin, they can remove. The name predates the second.
    /// </summary>
    public int DroppedNotifierEventCount { get; set; }

    /// <summary>When a notifier event was last dropped for this connection, UTC.</summary>
    public DateTime? LastDroppedNotifierEventAt { get; set; }

    /// <summary>When the user dismissed the "remove your notifier" notice, UTC. It never returns.</summary>
    public DateTime? NotifierNoticeDismissedAt { get; set; }

    // The agent's health, as of its last heartbeat (printlog-bridge#37). Each heartbeat replaces
    // all of it except the software names and versions, and an agent that sends none (an older
    // bridge) leaves it null.

    /// <summary>How the agent reaches the printer: <c>local</c> or <c>remote</c>.</summary>
    [MaxLength(20)]
    public string? Mode { get; set; }

    [MaxLength(50)]
    public string? KlipperVersion { get; set; }

    [MaxLength(50)]
    public string? MoonrakerVersion { get; set; }

    // The same, for any adapter (printlog-bridge#82). Firmware runs the printer (Klipper, Prusa's,
    // Bambu's); the host software is what the bridge talks to (Moonraker, OctoPrint, PrusaLink).
    // Unlike the rest of the health, a heartbeat without one keeps the stored value (#80).

    [MaxLength(50)]
    public string? FirmwareName { get; set; }

    [MaxLength(50)]
    public string? FirmwareVersion { get; set; }

    [MaxLength(50)]
    public string? HostSoftwareName { get; set; }

    [MaxLength(50)]
    public string? HostSoftwareVersion { get; set; }

    /// <summary>The printer model the slicer wrote in the last job's file, such as <c>InfiMech EX</c>.</summary>
    [MaxLength(100)]
    public string? PrinterModel { get; set; }

    /// <summary>Events and pictures waiting in the agent's outbox to be sent.</summary>
    public int? OutboxDepth { get; set; }

    /// <summary>When the oldest of them was queued, UTC: the heartbeat's time less its age.</summary>
    public DateTime? OutboxOldestQueuedAt { get; set; }

    /// <summary>The last warning or error the agent logged for this printer, scrubbed by the agent.</summary>
    [MaxLength(500)]
    public string? LastError { get; set; }

    /// <summary>When it was logged, UTC.</summary>
    public DateTime? LastErrorAt { get; set; }

    /// <summary>Warnings and errors logged since the agent's previous heartbeat that got through.</summary>
    public int? ErrorCount { get; set; }

    /// <summary>Whether a webcam frame is added to each print.</summary>
    public bool? Snapshots { get; set; }

    /// <summary>Whether each print's slicer settings are sent.</summary>
    public bool? SlicerSettings { get; set; }
}
