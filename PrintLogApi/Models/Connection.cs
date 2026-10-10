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
}
