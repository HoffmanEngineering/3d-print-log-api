using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models;

/// <summary>
/// An Auth0 pairing token already exchanged for a device token (#148), so it cannot be exchanged
/// again. Keyed on a hash of its <c>jti</c>, or of the whole token when it has none; never the
/// token itself. In the database rather than memory so it holds across instances. A row is
/// useless once <see cref="ExpiresAt"/> passes (the token is refused as too old by then), and
/// pairing deletes those as it goes.
/// </summary>
public class PairingTokenUse
{
    [Key]
    public long Id { get; set; }

    /// <summary>SHA-256, lowercase hex, of <c>jti:&lt;jti&gt;</c> or <c>token:&lt;token&gt;</c>.</summary>
    [Required]
    [MaxLength(64)]
    public string TokenKeyHash { get; set; } = null!;

    /// <summary>After this the pairing policy refuses the token anyway (its <c>iat</c> is too old).</summary>
    public DateTime ExpiresAt { get; set; }
}
