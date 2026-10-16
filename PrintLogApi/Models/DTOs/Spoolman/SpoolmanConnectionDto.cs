namespace PrintLogApi.Models.DTOs.Spoolman;

/// <summary>A Spoolman connection as the owner sees it. Never carries the secret.</summary>
public class SpoolmanConnectionDto
{
    public const string ActiveState = "active";
    public const string DisabledState = "disabled";
    public const string RevokedState = "revoked";

    public Guid Id { get; set; }

    /// <summary>The printer it is for, or null once that printer was deleted.</summary>
    public long? PrinterId { get; set; }

    /// <summary>The printer's current name, or null once that printer was deleted.</summary>
    public string? PrinterName { get; set; }

    /// <summary>The printer's name when the connection was made.</summary>
    public string? Name { get; set; }

    public DateTime CreatedDate { get; set; }

    /// <summary>When the printer last connected with it, or null if it never has.</summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary><c>active</c>, <c>disabled</c> (paused, see <see cref="DisabledReason"/>) or <c>revoked</c>.</summary>
    public string? State { get; set; }

    /// <summary>Why a disabled connection is paused: <c>tier</c> when Free allows only one.</summary>
    public string? DisabledReason { get; set; }

    public DateTime? DisabledAt { get; set; }

    public DateTime? RevokedAt { get; set; }
}

/// <summary>
/// A newly made connection: the only response that ever carries the secret. It cannot be shown
/// again, so a lost secret means reconnecting, which rotates it.
/// </summary>
public class SpoolmanConnectionCreatedDto : SpoolmanConnectionDto
{
    /// <summary>The secret, URL-userinfo safe (base64url). Shown once.</summary>
    public string? Secret { get; set; }

    /// <summary>The <c>server</c> value for <c>moonraker.conf</c>, secret included.</summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// The text to paste at the bottom of <c>moonraker.conf</c>: the <c>[spoolman]</c> section,
    /// followed by a <c>[notifier 3d_print_log]</c> section that logs prints unless the printer
    /// has a printlog-bridge connection (the bridge logs them itself).
    /// </summary>
    public string? ConfigBlock { get; set; }
}
