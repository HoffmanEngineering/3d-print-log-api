using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// A user's outbound webhook for print events (#235), such as a Home Assistant webhook trigger.
///
/// Not a TimestampEntity, for the same reason as <see cref="SpoolmanConnection"/>: there is no
/// meaningful "updated by", and its two FKs to Users would add paths from Users to this table
/// beside <see cref="UserId"/>'s cascade.
/// </summary>
public class WebhookSubscription
{
    [Key]
    public Guid Id { get; set; }

    public long UserId { get; set; }
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;

    /// <summary>
    /// Where events are posted. Checked against the SSRF policy when saved and again on every
    /// connection. Its path can itself be a secret (a Home Assistant webhook id), so it is never
    /// logged or put in a notification; the host is.
    /// </summary>
    [Required]
    [MaxLength(2048)]
    public string Url { get; set; } = null!;

    /// <summary>The user's label for it.</summary>
    [MaxLength(100)]
    public string? Description { get; set; }

    /// <summary>The events it receives.</summary>
    public WebhookEvents Events { get; set; }

    public bool Enabled { get; set; }

    /// <summary>
    /// The signing secret, encrypted by <c>WebhookSecretProtector</c>. It has to be recoverable to
    /// sign, so unlike other credentials here it is not a hash.
    /// </summary>
    [Required]
    [MaxLength(256)]
    public string EncryptedSecret { get; set; } = null!;

    /// <summary>Deliveries in a row that failed after their retries. A success resets it.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>When the last delivery (after its retries) finished, UTC.</summary>
    public DateTime? LastDeliveryAt { get; set; }

    /// <summary>The HTTP status of the last delivery's final attempt; null when no response came back.</summary>
    public int? LastStatusCode { get; set; }

    /// <summary>
    /// <c>success</c>, <c>http_error</c>, <c>timeout</c>, <c>connection_failed</c> or
    /// <c>blocked_address</c>.
    /// </summary>
    [MaxLength(32)]
    public string? LastDeliveryResult { get; set; }

    /// <summary>
    /// When repeated failures turned it off, UTC. Cleared when the user turns it back on, which
    /// also resets <see cref="ConsecutiveFailures"/>.
    /// </summary>
    public DateTime? AutoDisabledAt { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime UpdatedDate { get; set; }
}

/// <summary>The events a subscription receives. Stored as an int; strings on the wire.</summary>
[Flags]
public enum WebhookEvents
{
    None = 0,
    PrintStarted = 1,
    PrintFinished = 2,
    PrintFailed = 4,
    All = PrintStarted | PrintFinished | PrintFailed,
}
