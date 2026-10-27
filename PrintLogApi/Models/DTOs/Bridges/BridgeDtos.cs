using PrintLogApi.Models.DTOs.Connection;

namespace PrintLogApi.Models.DTOs.Bridges;

/// <summary>
/// What a bridge sends on check-in (<c>PUT /api/bridges/me</c>). Every field is optional: a field
/// that is missing or null keeps what is stored, so <c>{}</c> just fetches the desired printers.
/// A list that is sent replaces the stored one; an empty list clears it.
/// </summary>
public class BridgeCheckInDto
{
    /// <summary>The bridge's version, such as <c>0.3.0</c>: up to 50 of letters, digits and <c>. + -</c>.</summary>
    public string? AgentVersion { get; set; }

    /// <summary>How it runs, such as <c>local</c> or <c>remote</c>: lower-case letters, digits and dashes, up to 20.</summary>
    public string? Mode { get; set; }

    /// <summary>The name of the machine it runs on, up to 100 characters, no control characters.</summary>
    public string? HostName { get; set; }

    /// <summary>
    /// The private networks it can see, as CIDRs: IPv4 /24 or smaller (10/8, 172.16/12,
    /// 192.168/16, 169.254/16), IPv6 /64 or smaller in fc00::/7. At most 16. Never host lists.
    /// </summary>
    public List<string?>? Networks { get; set; }

    /// <summary>
    /// The printers it recognized on its networks, at most 50. Only printers an adapter
    /// recognizes, never other hosts. Replaces the stored list.
    /// </summary>
    public List<DiscoveredPrinterReportDto?>? Discovered { get; set; }

    /// <summary>When it last finished an active sweep ("Scan now"). A time in the future counts as now.</summary>
    public DateTimeOffset? LastSweepAt { get; set; }
}

/// <summary>One printer a bridge found. The identity is computed from <c>serial</c>, else <c>mainboardId</c>, else the MACs and port, else the address and port.</summary>
public class DiscoveredPrinterReportDto
{
    /// <summary>The adapter that recognized it: <c>moonraker</c>, <c>octoprint</c>, <c>prusalink</c>, <c>bambu</c> or <c>sdcp</c>.</summary>
    public string? Kind { get; set; }

    /// <summary>Its IP address: a private IPv4 address, or an IPv6 unique-local one, with no zone.</summary>
    public string? Address { get; set; }

    /// <summary>The port the adapter talks to, 1 to 65535.</summary>
    public int? Port { get; set; }

    /// <summary>The model it reports, up to 100 characters.</summary>
    public string? Model { get; set; }

    /// <summary>The name it advertises, up to 100 characters.</summary>
    public string? Name { get; set; }

    /// <summary>Its serial number (Bambu), up to 64 of letters, digits and <c>. _ : -</c>.</summary>
    public string? Serial { get; set; }

    /// <summary>Its mainboard id (SDCP), same rules as <c>serial</c>.</summary>
    public string? MainboardId { get; set; }

    /// <summary>Its MAC addresses (Moonraker's host), at most 8.</summary>
    public List<string?>? Macs { get; set; }

    /// <summary>The bridge already watches it from its config file.</summary>
    public bool? Configured { get; set; }
}

/// <summary>An entry the check-in dropped, and why. The rest of the check-in was stored.</summary>
public class RejectedEntryDto
{
    /// <summary>Which entry, such as <c>discovered[3]</c> or <c>networks[0]</c>.</summary>
    public string? Field { get; set; }

    /// <summary>
    /// Why: <c>invalid_address</c>, <c>address_not_private</c>, <c>invalid_kind</c>, <c>invalid_port</c>,
    /// <c>invalid_mac</c>, <c>invalid_serial</c>, <c>invalid_mainboard_id</c>, <c>invalid_text</c>,
    /// <c>duplicate_printer</c>, <c>invalid_network</c>, <c>network_not_private</c> or <c>network_too_large</c>.
    /// </summary>
    public string? Code { get; set; }
}

/// <summary>The answer to a check-in.</summary>
public class BridgeCheckInResponseDto
{
    /// <summary>This bridge's id (its device grant's id).</summary>
    public long BridgeId { get; set; }

    /// <summary>How often to check in, in seconds.</summary>
    public int CheckInSeconds { get; set; }

    /// <summary>How often to poll <c>GET /api/bridges/me/desired</c> between check-ins, in seconds.</summary>
    public int DesiredPollSeconds { get; set; }

    /// <summary>The printers the user chose for this bridge.</summary>
    public DesiredPrintersDto? Desired { get; set; }

    /// <summary>Entries of the report that were dropped.</summary>
    public List<RejectedEntryDto>? Rejected { get; set; }
}

/// <summary>
/// The printers a bridge should watch, besides those in its config file (which win when both
/// name the same printer). Credentials are not here: until they can be encrypted to the bridge's
/// key, the bridge asks for an access code or password locally.
/// </summary>
public class DesiredPrintersDto
{
    /// <summary>Opaque; changes whenever the list or the scan request does. Compare for equality only.</summary>
    public string? Version { get; set; }

    /// <summary>When the user last asked for an active sweep; run one when this is newer than your last.</summary>
    public DateTimeOffset? ScanRequestedAt { get; set; }

    public List<BridgePrinterDto>? Printers { get; set; }
}

/// <summary>A printer connected to a bridge from the app.</summary>
public class BridgePrinterDto
{
    /// <summary>Stable for as long as the printer stays connected; key local state (credentials) on it.</summary>
    public long Id { get; set; }

    /// <summary>The 3D Print Log printer its prints are logged to.</summary>
    public long PrinterId { get; set; }

    public string? PrinterName { get; set; }

    /// <summary>The adapter.</summary>
    public string? Kind { get; set; }

    /// <summary>A private IP address or a local host name (check that it resolves to a private address).</summary>
    public string? Host { get; set; }

    /// <summary>Null for the adapter's default.</summary>
    public int? Port { get; set; }

    public string? Serial { get; set; }

    /// <summary>The discovered printer's identity when it was connected from discovery, to follow it to a new address.</summary>
    public string? IdentityKey { get; set; }

    public List<string>? Macs { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The body of <c>POST /api/bridges/{id}/printers</c>.</summary>
public class ConnectBridgePrinterDto
{
    /// <summary>
    /// The discovered printer to connect. Its kind, address, port, serial and identity are used;
    /// <c>host</c>, <c>port</c> and <c>serial</c> here override them. Leave it out to connect a
    /// printer by hand, with <c>kind</c> and <c>host</c>.
    /// </summary>
    public long? DiscoveredPrinterId { get; set; }

    /// <summary>Your 3D Print Log printer to log to (create one with <c>POST /api/Printers</c> first).</summary>
    public long? PrinterId { get; set; }

    /// <summary>The adapter, when connecting by hand. With a discovered printer it must match, or be left out.</summary>
    public string? Kind { get; set; }

    /// <summary>
    /// A private IP address, or a host name that is a single label or ends in <c>.local</c>,
    /// <c>.lan</c>, <c>.home.arpa</c>, <c>.internal</c> or <c>.localdomain</c>.
    /// </summary>
    public string? Host { get; set; }

    public int? Port { get; set; }

    /// <summary>Required for <c>bambu</c>.</summary>
    public string? Serial { get; set; }
}

/// <summary>The body of <c>PATCH /api/bridges/{id}</c>. Null keeps a field; an empty string clears it.</summary>
public class UpdateBridgeDto
{
    /// <summary>Up to 100 characters. Cleared, the device's own name shows.</summary>
    public string? Name { get; set; }

    /// <summary>Up to 100 characters ("Workshop").</summary>
    public string? Location { get; set; }
}

/// <summary>A paired bridge, as the Bridges screen shows it.</summary>
public class BridgeDto
{
    /// <summary>The bridge's id, which is also its device grant's (revoke with <c>DELETE /api/device-grants/{id}</c>).</summary>
    public long Id { get; set; }

    /// <summary>What you named it, or null.</summary>
    public string? Name { get; set; }

    /// <summary>What the bridge calls itself.</summary>
    public string? DeviceName { get; set; }

    /// <summary><c>name</c>, else <c>deviceName</c>.</summary>
    public string? DisplayName { get; set; }

    public string? Location { get; set; }

    public string? AgentVersion { get; set; }

    /// <summary>The newest printlog-bridge release, or null when unknown.</summary>
    public string? LatestAgentVersion { get; set; }

    public bool UpdateAvailable { get; set; }

    public string? Mode { get; set; }

    public string? HostName { get; set; }

    public List<string>? Networks { get; set; }

    /// <summary>Online when it checked in within the last 15 minutes.</summary>
    public ConnectionStatus Status { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ScanRequestedAt { get; set; }

    public DateTimeOffset? LastSweepAt { get; set; }

    /// <summary>Printers connected to it from the app.</summary>
    public int PrinterCount { get; set; }

    /// <summary>Connections (printers it watches, from the app or its config file) whose last heartbeat came from it.</summary>
    public int ConnectionCount { get; set; }

    /// <summary>Printers it found, all states.</summary>
    public int DiscoveredCount { get; set; }

    /// <summary>Printers it found that are not connected, not in its config file and not ignored.</summary>
    public int DiscoveredNotConnectedCount { get; set; }
}

/// <summary>Where a discovered printer stands.</summary>
public enum DiscoveredPrinterState
{
    /// <summary>Found, and nothing done with it yet: a suggestion.</summary>
    New = 1,

    /// <summary>Connected to this bridge from the app (<c>printerEntryId</c>).</summary>
    Connected = 2,

    /// <summary>The bridge watches it from its config file; the app can't change that.</summary>
    Configured = 3,

    /// <summary>You asked not to be shown it.</summary>
    Ignored = 4,
}

/// <summary>A printer a bridge found on its network.</summary>
public class DiscoveredPrinterDto
{
    public long Id { get; set; }

    public string? Kind { get; set; }

    public string? Address { get; set; }

    public int? Port { get; set; }

    public string? Model { get; set; }

    public string? Name { get; set; }

    public string? Serial { get; set; }

    public string? MainboardId { get; set; }

    public List<string>? Macs { get; set; }

    public string? IdentityKey { get; set; }

    public DiscoveredPrinterState State { get; set; }

    /// <summary>The connected entry, when <c>state</c> is Connected.</summary>
    public long? PrinterEntryId { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>The last check-in that reported it.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>The answer to "Scan now".</summary>
public class BridgeScanDto
{
    public DateTimeOffset ScanRequestedAt { get; set; }
}
