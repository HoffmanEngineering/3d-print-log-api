using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// A paired device (#148): a printlog-bridge, or later a Home Assistant install, that the user
/// approved through the Auth0 device flow and that now holds a scoped device token. One row per
/// device per user; re-pairing the same <see cref="DeviceId"/> rotates the token on this row, so
/// the grant's id is the device's stable identity (the bridge record of printlog-bridge#103 hangs
/// off it).
///
/// Not to be confused with <see cref="DeviceToken"/>, which is an FCM push registration. Not a
/// TimestampEntity, for the same reason as that one: a device has no meaningful "updated by".
/// </summary>
public class DeviceGrant
{
    public const int NameMaxLength = 100;

    [Key]
    public long Id { get; set; }

    public long UserId { get; set; }
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;

    /// <summary>The id the device generated for itself and keeps across restarts and re-pairs.</summary>
    public Guid DeviceId { get; set; }

    /// <summary>What the device calls itself ("printlog-bridge on voron"). Self-reported, shown to the user.</summary>
    [Required]
    [MaxLength(NameMaxLength)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// SHA-256 of the device token, lowercase hex. The token itself is never stored. It carries
    /// 256 random bits, so an unsalted hash is not guessable, and it is what the per-request
    /// lookup matches on. A concurrency token, with <see cref="PreviousTokenHash"/>: a re-pair
    /// rotates the pair it read, and fails (then reads again) if a racing re-pair or the new
    /// token's first use changed either since (api#287).
    /// </summary>
    [Required]
    [MaxLength(64)]
    [ConcurrencyCheck]
    public string TokenHash { get; set; } = null!;

    /// <summary>The granted device scopes, space-separated and sorted (<c>DeviceScopes</c>).</summary>
    [Required]
    [MaxLength(500)]
    public string Scopes { get; set; } = null!;

    /// <summary>The Auth0 client (<c>azp</c>) the device paired through, which tells a bridge from a later Home Assistant app.</summary>
    [MaxLength(128)]
    public string? ClientId { get; set; }

    public DateTime CreatedDate { get; set; }

    /// <summary>When the current token was issued: the first pairing, or the latest re-pair.</summary>
    public DateTime PairedDate { get; set; }

    /// <summary>Last authenticated request, stamped at most every few minutes.</summary>
    public DateTime? LastUsedDate { get; set; }

    /// <summary>
    /// The token a re-pair replaced (api#287), still accepted until <see cref="PreviousTokenExpiresAt"/>
    /// or the first use of <see cref="TokenHash"/>, whichever comes first, so a device whose
    /// re-pair reply was lost is not stranded. Same format as <see cref="TokenHash"/>. Deleting
    /// the row (a revoke) ends both. A concurrency token: see <see cref="TokenHash"/>.
    /// </summary>
    [MaxLength(64)]
    [ConcurrencyCheck]
    public string? PreviousTokenHash { get; set; }

    /// <summary>When <see cref="PreviousTokenHash"/> stops working, whether or not the new token was used.</summary>
    public DateTime? PreviousTokenExpiresAt { get; set; }

    /// <summary>When the owner was last told this device paired (api#287): the per-device throttle on that notice.</summary>
    public DateTime? PairingNoticeAt { get; set; }
}
