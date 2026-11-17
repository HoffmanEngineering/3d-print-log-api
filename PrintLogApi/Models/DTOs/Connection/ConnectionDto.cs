namespace PrintLogApi.Models.DTOs.Connection;

/// <summary>Whether a connection's agent is still sending heartbeats.</summary>
public enum ConnectionStatus
{
    /// <summary>A heartbeat arrived within the last 15 minutes.</summary>
    Online = 1,

    /// <summary>No heartbeat for 15 minutes or more.</summary>
    Stale = 2,
}

/// <summary>A connector attached to one of your printers.</summary>
public class ConnectionDto
{
    public Guid Id { get; set; }

    /// <summary>
    /// The connector type, lower-case. printlog-bridge registers <c>moonraker</c>, <c>octoprint</c>,
    /// <c>prusalink</c>, <c>bambu</c>, <c>sdcp</c> (Elegoo), <c>duet</c>, <c>flashforge</c> or <c>anycubic</c>.
    /// </summary>
    public string Kind { get; set; } = null!;

    /// <summary>The agent's own stable id for this printer.</summary>
    public string InstanceId { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string? AgentVersion { get; set; }

    /// <summary>
    /// The newest release of this connection's agent (printlog-bridge for <c>moonraker</c>), as
    /// <c>X.Y.Z</c>, or null when 3D Print Log doesn't know it. Read from the agent's GitHub tags
    /// every few hours, so agents learn of an update from their check-in and never call GitHub.
    /// </summary>
    public string? LatestAgentVersion { get; set; }

    /// <summary>Whether <see cref="AgentVersion"/> is older than <see cref="LatestAgentVersion"/>.</summary>
    public bool UpdateAvailable { get; set; }

    /// <summary>
    /// The paired bridge whose device token sent the last heartbeat (<c>GET /api/bridges/{id}</c>),
    /// or null for an API-key agent or a bridge that has not checked in.
    /// </summary>
    public long? BridgeId { get; set; }

    /// <summary>The printer this connection logs prints for, or null when it is not bound to one.</summary>
    public long? PrinterId { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    /// <summary>When the agent last sent a heartbeat.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Stale after 15 minutes without a heartbeat.</summary>
    public ConnectionStatus Status { get; set; }

    /// <summary>
    /// Events dropped because this connection logs the same printer: the Moonraker notifier's on a
    /// <c>moonraker</c> connection, the OctoPrint Webhooks plugin's on an <c>octoprint</c> one. Each
    /// job is logged once, by the connection.
    /// </summary>
    public int DroppedNotifierEventCount { get; set; }

    /// <summary>When a notifier event was last dropped.</summary>
    public DateTimeOffset? LastDroppedNotifierEventAt { get; set; }

    /// <summary>When you dismissed the notice about the notifier.</summary>
    public DateTimeOffset? NotifierNoticeDismissedAt { get; set; }

    /// <summary>
    /// Whether to suggest removing the integration this connection duplicates (the <c>[notifier]</c>
    /// section of moonraker.conf for <c>moonraker</c>, the OctoPrint Webhooks plugin for
    /// <c>octoprint</c>): events were dropped and the notice has not been dismissed.
    /// </summary>
    public bool ShowNotifierNotice { get; set; }

    // The agent's health as of its last heartbeat, null when it sent none (printlog-bridge#37).

    /// <summary>How the agent reaches the printer: <c>local</c> or <c>remote</c>.</summary>
    public string? Mode { get; set; }

    public string? KlipperVersion { get; set; }

    public string? MoonrakerVersion { get; set; }

    /// <summary>The printer's firmware, such as <c>Klipper</c>, <c>Prusa-Firmware-Buddy</c> or <c>Bambu</c>.</summary>
    public string? FirmwareName { get; set; }

    public string? FirmwareVersion { get; set; }

    /// <summary>
    /// The software the agent talks to on the printer's host, such as <c>Moonraker</c>,
    /// <c>OctoPrint</c> or <c>PrusaLink</c>. Null when there is none apart from the firmware.
    /// </summary>
    public string? HostSoftwareName { get; set; }

    public string? HostSoftwareVersion { get; set; }

    /// <summary>The printer model the slicer wrote in the last job's file.</summary>
    public string? PrinterModel { get; set; }

    /// <summary>Events and pictures waiting in the agent's outbox to be sent.</summary>
    public int? OutboxDepth { get; set; }

    /// <summary>When the oldest of them was queued.</summary>
    public DateTimeOffset? OutboxOldestQueuedAt { get; set; }

    /// <summary>The last warning or error the agent logged for this printer.</summary>
    public string? LastError { get; set; }

    /// <summary>When it was logged.</summary>
    public DateTimeOffset? LastErrorAt { get; set; }

    /// <summary>Warnings and errors logged between the agent's last two heartbeats.</summary>
    public int? ErrorCount { get; set; }

    /// <summary>Whether a webcam frame is added to each print.</summary>
    public bool? Snapshots { get; set; }

    /// <summary>Whether each print's slicer settings are sent.</summary>
    public bool? SlicerSettings { get; set; }
}
