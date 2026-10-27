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

    /// <summary>
    /// Its P-256 public key for sealed credentials: standard base64 (with padding) of the DER
    /// SubjectPublicKeyInfo with an uncompressed point, 124 characters. Anything else is dropped
    /// (<c>rejected</c>: <c>invalid_public_key</c>) and the stored key kept. The first key is
    /// taken as is; a different one later is held pending until the owner confirms it. See
    /// docs/bridge-credential-sealing.md.
    /// </summary>
    public string? PublicKey { get; set; }

    /// <summary>Optional: lower-case hex SHA-256 of the key's SPKI bytes. When sent, it must match <c>publicKey</c>, or the key is dropped.</summary>
    public string? PublicKeyFingerprint { get; set; }

    /// <summary>The desired <c>version</c> that <c>credentialIssues</c> describes. Required with it; issues about another version are ignored.</summary>
    public string? CredentialIssuesVersion { get; set; }

    /// <summary>
    /// Credentials it can't use, at most 200. Replaces the stored issues of all its printers;
    /// an empty list clears them.
    /// </summary>
    public List<CredentialIssueReportDto?>? CredentialIssues { get; set; }
}

/// <summary>A credential a bridge can't use.</summary>
public class CredentialIssueReportDto
{
    /// <summary>The desired printer's <c>id</c>.</summary>
    public long? EntryId { get; set; }

    /// <summary>One of the entry's <c>secretFields</c>.</summary>
    public string? Field { get; set; }

    /// <summary>
    /// <c>missing</c> (needed, and no value it can open), <c>undecryptable</c> (a value that does not
    /// open), <c>invalid_plaintext</c> (it opened to something that is not a credential: invalid
    /// UTF-8 or a control character) or <c>rejected_by_printer</c> (it opened, and the printer
    /// refused it).
    /// </summary>
    public string? Code { get; set; }
}

/// <summary>A credential problem the bridge reported, as stored.</summary>
public class CredentialIssueDto
{
    public string? Field { get; set; }

    public string? Code { get; set; }
}

/// <summary>One sealed LAN credential of a desired printer.</summary>
public class BridgePrinterSecretDto
{
    /// <summary>The adapter's field, such as <c>access_code</c>.</summary>
    public string? Field { get; set; }

    /// <summary>
    /// The sealed value (<c>v1.</c>…). Only in the bridge's own desired list; the owner's
    /// endpoints never return it.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Sealed { get; set; }

    /// <summary>The fingerprint of the bridge key it was sealed to.</summary>
    public string? KeyFingerprint { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Sealed to a key the bridge no longer has: seal it again to the current key.</summary>
    public bool Stale { get; set; }
}

/// <summary>The body of <c>PUT /api/bridges/{id}/printers/{entryId}/secrets/{field}</c>.</summary>
public class SetBridgePrinterSecretDto
{
    /// <summary>
    /// The credential sealed in the browser to the bridge's <c>publicKey</c>
    /// (docs/bridge-credential-sealing.md), with associated data
    /// <c>printlog-credential:v1:{bridgeId}:{entryId}:{field}</c>. Never the plaintext.
    /// </summary>
    public string? Sealed { get; set; }

    /// <summary>
    /// The fingerprint of the key it was sealed to, computed by the sealer over the exact SPKI
    /// bytes it sealed to (never copied from the API): it must be the bridge's current key.
    /// </summary>
    public string? KeyFingerprint { get; set; }
}

/// <summary>The body of <c>POST /api/bridges/{id}/public-key/confirm</c>.</summary>
public class ConfirmBridgeKeyDto
{
    /// <summary>
    /// The pending key's fingerprint, computed by the web app from <c>pendingPublicKey</c> (and
    /// compared by the user with the one the bridge shows), never copied from the API.
    /// </summary>
    public string? Fingerprint { get; set; }
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

    /// <summary>The fingerprint of the public key values are sealed to, or null. If it isn't yours and nothing is pending, send <c>publicKey</c>.</summary>
    public string? PublicKeyFingerprint { get; set; }

    /// <summary>A newer key you reported, waiting for the owner to confirm it in the app; nothing is sealed to it until then.</summary>
    public string? PendingPublicKeyFingerprint { get; set; }
}

/// <summary>
/// The printers a bridge should watch, besides those in its config file (which win when both
/// name the same printer), with their LAN credentials sealed to the bridge's key.
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

    /// <summary>The credentials this kind takes, sealed with <c>PUT …/secrets/{field}</c>.</summary>
    public List<string>? SecretFields { get; set; }

    /// <summary>
    /// The sealed credentials. The bridge gets only those sealed to its current key, with
    /// <c>sealed</c>; the owner gets all of them without it, stale ones marked.
    /// </summary>
    public List<BridgePrinterSecretDto>? Secrets { get; set; }

    /// <summary>What the bridge last reported it can't use.</summary>
    public List<CredentialIssueDto>? CredentialIssues { get; set; }
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

/// <summary>
/// The body of <c>POST /api/bridges/{id}/printers/{entryId}/move</c>. Without
/// <c>discoveredPrinterId</c> or <c>host</c> the printer keeps its address, which suits a bridge
/// on the same network.
/// </summary>
public class MoveBridgePrinterDto
{
    /// <summary>Another of your bridges, to watch the printer from now on.</summary>
    public long? ToBridgeId { get; set; }

    /// <summary>
    /// The printer as the new bridge found it: its address, port, serial and identity are taken.
    /// It must be the same kind. <c>host</c> and <c>port</c> here override them.
    /// </summary>
    public long? DiscoveredPrinterId { get; set; }

    /// <summary>The address the new bridge reaches it at, when it differs: a private IP address or a local host name.</summary>
    public string? Host { get; set; }

    public int? Port { get; set; }

    /// <summary>
    /// The printer's credentials for the new bridge, by field: each sealed in the browser to the
    /// new bridge's current key with associated data
    /// <c>printlog-credential:v1:{toBridgeId}:{entryId}:{field}</c>, exactly as for
    /// <c>PUT …/secrets/{field}</c>. A field the printer needs to connect, and that was set, must
    /// be here (<c>secrets_required</c> otherwise); others that aren't sent are dropped.
    /// </summary>
    public Dictionary<string, string?>? Secrets { get; set; }

    /// <summary>The new bridge's key fingerprint the values were sealed to, computed in the browser. Required with <c>secrets</c>.</summary>
    public string? KeyFingerprint { get; set; }
}

/// <summary>The answer to a move.</summary>
public class MovedBridgePrinterDto
{
    /// <summary>The entry, as the new bridge has it. Its id is unchanged.</summary>
    public BridgePrinterDto? Printer { get; set; }

    public long FromBridgeId { get; set; }

    public long ToBridgeId { get; set; }

    /// <summary>
    /// The credentials that were set and are now gone because the move didn't send them (they were
    /// sealed to the old bridge's key). Seal one again to the new bridge if the printer needs it.
    /// </summary>
    public List<string>? DroppedSecretFields { get; set; }
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

    /// <summary>
    /// Its public key for sealing credentials (base64 SPKI, P-256), or null when it has reported
    /// none (an older bridge: it can't take sealed credentials).
    /// </summary>
    public string? PublicKey { get; set; }

    /// <summary>
    /// Lower-case hex SHA-256 of the key, for convenience only: the web app computes the
    /// fingerprint it shows and sends from <c>publicKey</c> itself.
    /// </summary>
    public string? PublicKeyFingerprint { get; set; }

    /// <summary>When this key became the one values are sealed to.</summary>
    public DateTimeOffset? PublicKeyChangedAt { get; set; }

    /// <summary>
    /// A different key the bridge reported since, held until you confirm it
    /// (<c>POST /api/bridges/{id}/public-key/confirm</c>), or null.
    /// </summary>
    public string? PendingPublicKey { get; set; }

    /// <summary>Convenience only, like <c>publicKeyFingerprint</c>.</summary>
    public string? PendingPublicKeyFingerprint { get; set; }

    /// <summary>When the pending key was first reported.</summary>
    public DateTimeOffset? PendingPublicKeyAt { get; set; }

    /// <summary>
    /// True when the bridge reported more new keys today than the daily cap: the API stopped
    /// replacing the pending key (it keeps the first one past the cap) until the UTC day ends.
    /// </summary>
    public bool KeyChangesThrottled { get; set; }

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

    /// <summary>
    /// Distinct jobs it reported for a printer another of your bridges watches: two bridges watch
    /// that printer. Each was logged (as the other bridge's print when that one already logged
    /// the job), so nothing is lost, but a job can be logged twice. Non-zero: remove that printer
    /// from this bridge (most likely its config file). Never reset.
    /// </summary>
    public int SharedJobCount { get; set; }

    /// <summary>When such a job was last reported. A warning can be hidden once this is old: it stopped happening.</summary>
    public DateTimeOffset? LastSharedJobAt { get; set; }

    /// <summary>The printer of the last such job.</summary>
    public long? LastSharedJobPrinterId { get; set; }
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
