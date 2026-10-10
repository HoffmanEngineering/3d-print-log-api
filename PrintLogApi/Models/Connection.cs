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

    [Key]
    public Guid Id { get; set; }

    public long UserId { get; set; }
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;

    /// <summary>The connector type, lower-case: <c>moonraker</c>, <c>octoprint</c>.</summary>
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

    public DateTime CreatedDate { get; set; }

    /// <summary>The last heartbeat, UTC.</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>
    /// Moonraker notifier events dropped because this connection was logging the same printer
    /// (#150). Non-zero means the user still has a <c>[notifier]</c> section they can remove.
    /// </summary>
    public int DroppedNotifierEventCount { get; set; }

    /// <summary>When a notifier event was last dropped for this connection, UTC.</summary>
    public DateTime? LastDroppedNotifierEventAt { get; set; }

    /// <summary>When the user dismissed the "remove your notifier" notice, UTC. It never returns.</summary>
    public DateTime? NotifierNoticeDismissedAt { get; set; }
}
