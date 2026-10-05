using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

public enum EmailSuppressionReason : byte
{
    Bounce = 1,
    Complaint = 2,
    Manual = 3,
}

/// <summary>
/// An address we must never email again. Keyed by a peppered HMAC of the normalized address rather
/// than by user, so it survives account deletion: someone who complained, deleted their account and
/// signed up again is still suppressed. The hash is pseudonymous, not anonymous — these rows are
/// personal data, kept to honor the person's objection.
/// </summary>
public class EmailSuppression
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>Lowercase hex HMAC-SHA-256 of the normalized address.</summary>
    [MaxLength(64)]
    public string EmailHash { get; set; } = null!;

    public EmailSuppressionReason Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Bounce subtype or feedback id, for support.</summary>
    [MaxLength(256)]
    public string? Detail { get; set; }
}
