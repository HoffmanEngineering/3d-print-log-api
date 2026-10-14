using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// One network a user's Spoolman connections have connected from (#175), kept to spot account
/// sharing or resale: more than five in a rolling week sends the admin an email. Nothing is
/// blocked.
///
/// The network is a keyed hash of its /24 (IPv4) or /48 (IPv6) prefix; no address is stored.
/// Rows not seen for 30 days are deleted the next time the user's connections are seen.
/// </summary>
public class SpoolmanNetworkSighting
{
    /// <summary>How long a sighting is kept after it was last seen.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    [Key]
    public long Id { get; set; }

    public long UserId { get; set; }
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;

    /// <summary>Lower-case hex HMAC-SHA-256 of the network prefix. See <c>SpoolmanNetwork</c>.</summary>
    [Required]
    [MaxLength(64)]
    public string NetworkHash { get; set; } = null!;

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }
}
