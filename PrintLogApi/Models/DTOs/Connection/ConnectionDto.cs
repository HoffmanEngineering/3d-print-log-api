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

    /// <summary>The connector type, lower-case: <c>moonraker</c>, <c>octoprint</c>.</summary>
    public string Kind { get; set; } = null!;

    /// <summary>The agent's own stable id for this printer.</summary>
    public string InstanceId { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string? AgentVersion { get; set; }

    /// <summary>The printer this connection logs prints for, or null when it is not bound to one.</summary>
    public long? PrinterId { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    /// <summary>When the agent last sent a heartbeat.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Stale after 15 minutes without a heartbeat.</summary>
    public ConnectionStatus Status { get; set; }
}
