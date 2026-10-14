using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// One printer's credential for the Spoolman-compatible API (#175). Moonraker carries the secret
/// as the password of the URL in its <c>[spoolman] server:</c> line, so it is narrow by design:
/// one printer, the <c>/spoolman</c> routes only, revocable, and stored only as a hash.
///
/// Not a TimestampEntity, for the same reason as <see cref="Connection"/>: there is no meaningful
/// "updated by", and TimestampEntity's two required FKs to Users would be a second cascade path
/// from Users alongside the printer's (SQL Server error 1785).
/// </summary>
public class SpoolmanConnection
{
    /// <summary>
    /// The <see cref="DisabledReason"/> a tier downgrade writes. Re-subscribing clears exactly
    /// these and no other reason, so a future reason (an admin pause) is never undone by billing.
    /// </summary>
    public const string TierDisabledReason = "tier";

    [Key]
    public Guid Id { get; set; }

    public long UserId { get; set; }
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;

    /// <summary>
    /// The printer the credential is for. Null only once that printer is deleted: deleting a
    /// printer revokes its connection and unbinds it, and the row stays so the user still sees it
    /// in their list.
    /// </summary>
    public long? PrinterId { get; set; }
    [ForeignKey("PrinterId")]
    public Printer? Printer { get; set; }

    /// <summary>The printer's name when the connection was made.</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = null!;

    /// <summary>Lower-case hex SHA-256 of the secret. The secret itself is never stored.</summary>
    [Required]
    [MaxLength(64)]
    public string SecretHash { get; set; } = null!;

    [Required]
    [MaxLength(16)]
    public string HashAlgorithm { get; set; } = null!;

    public DateTime CreatedDate { get; set; }

    /// <summary>When the printer last authenticated with this connection, UTC. Throttled.</summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>When the user revoked it, or rotated it, or deleted its printer, UTC. Final.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// When it was paused, UTC. Unlike revocation this is reversible: a downgrade to Free pauses
    /// all but the oldest connection, and re-subscribing resumes them, so the user never has to
    /// edit <c>moonraker.conf</c> on each printer again.
    /// </summary>
    public DateTime? DisabledAt { get; set; }

    /// <summary>Why it is paused; <see cref="TierDisabledReason"/> today.</summary>
    [MaxLength(50)]
    public string? DisabledReason { get; set; }

    [NotMapped]
    public bool IsRevoked => RevokedAt.HasValue;

    [NotMapped]
    public bool IsActive => !RevokedAt.HasValue && !DisabledAt.HasValue;
}
