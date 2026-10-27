using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// A paired printlog-bridge (printlog-bridge#100, #103): one per device grant that has checked in
/// with <c>bridge:sync</c>. It shares the grant's id, so the bridge identity is exactly what a
/// device token already carries (<c>device_grant_id</c>), and revoking the grant deletes it (a
/// database cascade), together with what it found and what it was told to watch.
///
/// A table of its own rather than columns on <see cref="DeviceGrant"/>: the grant is the
/// credential every device request looks up, and not every grant is a bridge (Home Assistant
/// pairs too). Check-ins write here, never to the credential row.
/// </summary>
public class Bridge
{
    public const int NameMaxLength = 100;
    public const int LocationMaxLength = 100;
    public const int HostNameMaxLength = 100;
    public const int AgentVersionMaxLength = 50;
    public const int ModeMaxLength = 20;

    /// <summary>The networks a bridge may report (<see cref="Networks"/>).</summary>
    public const int MaxNetworks = 16;

    /// <summary>The printers one check-in may report as found.</summary>
    public const int MaxDiscoveredPrinters = 50;

    /// <summary>The printers the user may connect to one bridge.</summary>
    public const int MaxPrinters = 50;

    /// <summary>The printers the user may ignore on one bridge.</summary>
    public const int MaxIgnoredPrinters = 200;

    /// <summary>The device grant's id: one bridge per grant, created by its first check-in.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public long Id { get; set; }

    public DeviceGrant Grant { get; set; } = null!;

    /// <summary>What the user calls it ("Garage NAS"), or null to show the device's own name.</summary>
    [MaxLength(NameMaxLength)]
    public string? Name { get; set; }

    /// <summary>Where it is, as the user labels it ("Workshop").</summary>
    [MaxLength(LocationMaxLength)]
    public string? Location { get; set; }

    [MaxLength(AgentVersionMaxLength)]
    public string? AgentVersion { get; set; }

    /// <summary>How the bridge runs, as it reports it (<c>local</c>, <c>remote</c>).</summary>
    [MaxLength(ModeMaxLength)]
    public string? Mode { get; set; }

    /// <summary>The name of the machine the bridge runs on, as it reports it.</summary>
    [MaxLength(HostNameMaxLength)]
    public string? HostName { get; set; }

    /// <summary>
    /// The private networks the bridge can see, space-separated CIDRs normalized to the network
    /// address: IPv4 /24 or smaller, IPv6 /64 or smaller. Never host lists.
    /// </summary>
    [MaxLength(800)]
    public string? Networks { get; set; }

    public DateTime CreatedDate { get; set; }

    /// <summary>The last check-in, UTC.</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>
    /// Changes whenever the bridge's desired printers (or the scan request) change; the bridge
    /// sees it as an opaque version. A fresh GUID per change, so concurrent edits need no counter.
    /// </summary>
    public Guid DesiredRevision { get; set; }

    /// <summary>When the user last asked for an active sweep ("Scan now"), UTC.</summary>
    public DateTime? ScanRequestedAt { get; set; }

    /// <summary>When the bridge last finished an active sweep, as it reports it, UTC.</summary>
    public DateTime? LastSweepAt { get; set; }

    /// <summary>
    /// The bridge's P-256 public key for sealed LAN credentials (story 14,
    /// docs/bridge-credential-sealing.md): base64 of its DER SubjectPublicKeyInfo, exactly as
    /// <c>SealedCredentials.TryParsePublicKey</c> accepts it. Null until a bridge reports one.
    /// The private key never leaves the bridge.
    /// </summary>
    [MaxLength(200)]
    public string? PublicKey { get; set; }

    /// <summary>Lower-case hex SHA-256 of the SPKI bytes; what a sealed value names as the key it was sealed to.</summary>
    [MaxLength(64)]
    public string? PublicKeyFingerprint { get; set; }

    /// <summary>When the current key became current (first reported, or confirmed), UTC.</summary>
    public DateTime? PublicKeyChangedAt { get; set; }

    /// <summary>
    /// A different key the bridge reported after it already had one: held until the owner
    /// confirms it by its fingerprint, because a stolen device token could report its own key.
    /// Nothing is sealed to it until then. Same format as <see cref="PublicKey"/>.
    /// </summary>
    [MaxLength(200)]
    public string? PendingPublicKey { get; set; }

    [MaxLength(64)]
    public string? PendingPublicKeyFingerprint { get; set; }

    /// <summary>When the pending key was first reported, UTC.</summary>
    public DateTime? PendingPublicKeyAt { get; set; }

    /// <summary>The UTC day <see cref="KeyChangesToday"/> counts.</summary>
    public DateTime? KeyChangesDay { get; set; }

    /// <summary>Key changes (first key, new pending key, confirmation) on <see cref="KeyChangesDay"/>, for telemetry.</summary>
    public int KeyChangesToday { get; set; }

    /// <summary>The UTC day the owner was last told about a pending key; at most one notice a day.</summary>
    public DateTime? KeyNotifiedDay { get; set; }

    /// <summary>
    /// Distinct jobs this bridge reported for a printer another of the user's bridges is bound to:
    /// jobs that bridge also reports, so two bridges watch one printer (printlog-bridge#103). They
    /// are logged, never refused (the job gets the bound bridge's print when that bridge already
    /// logged it). A job counts once however often it is sent (start, finish, claim), by its
    /// external pair (<see cref="BridgeSharedJob"/>); a job sent without a pair counts each time.
    /// A job that started before the printer moved away from this bridge isn't counted. Never
    /// reset; <see cref="LastSharedJobAt"/> says whether it is still happening. Non-zero means this
    /// bridge also watches a printer it shouldn't (its config file, most likely).
    /// </summary>
    public int SharedJobCount { get; set; }

    /// <summary>When such a job was last reported, UTC, including a job already counted.</summary>
    public DateTime? LastSharedJobAt { get; set; }

    /// <summary>The 3D Print Log printer of the last such job. A value, not a key: the printer may be deleted.</summary>
    public long? LastSharedJobPrinterId { get; set; }

    public ICollection<BridgeDiscoveredPrinter>? DiscoveredPrinters { get; set; }

    public ICollection<BridgePrinter>? Printers { get; set; }
}

/// <summary>
/// A printer the bridge found on its network and reported in its last check-in. A suggestion
/// only: nothing here is ever watched until the user connects it (<see cref="BridgePrinter"/>).
/// Each check-in replaces the bridge's list; a printer still reported keeps its row.
/// </summary>
public class BridgeDiscoveredPrinter
{
    [Key]
    public long Id { get; set; }

    public long BridgeId { get; set; }
    [ForeignKey("BridgeId")]
    public Bridge Bridge { get; set; } = null!;

    /// <summary>The server-computed stable identity (<c>BridgeRules.IdentityKey</c>), unique per bridge.</summary>
    [Required]
    [MaxLength(200)]
    public string IdentityKey { get; set; } = null!;

    /// <summary>The adapter that recognized it: one of <see cref="Connection.BridgeKinds"/>.</summary>
    [Required]
    [MaxLength(50)]
    public string Kind { get; set; } = null!;

    /// <summary>Its private IP address.</summary>
    [Required]
    [MaxLength(45)]
    public string Address { get; set; } = null!;

    public int? Port { get; set; }

    [MaxLength(100)]
    public string? Model { get; set; }

    /// <summary>The name the printer advertises.</summary>
    [MaxLength(100)]
    public string? Name { get; set; }

    [MaxLength(64)]
    public string? Serial { get; set; }

    [MaxLength(64)]
    public string? MainboardId { get; set; }

    /// <summary>Its MAC addresses, space-separated, lower-case with colons.</summary>
    [MaxLength(200)]
    public string? Macs { get; set; }

    /// <summary>The bridge already watches it from its config file, which the app can't change.</summary>
    public bool Configured { get; set; }

    public DateTime FirstSeenAt { get; set; }

    /// <summary>The last check-in that reported it, UTC.</summary>
    public DateTime LastSeenAt { get; set; }
}

/// <summary>
/// A printer the user told a bridge to stop suggesting, by identity, so it stays hidden while it
/// drops out of a check-in and comes back.
/// </summary>
public class BridgeIgnoredPrinter
{
    [Key]
    public long Id { get; set; }

    public long BridgeId { get; set; }
    [ForeignKey("BridgeId")]
    public Bridge Bridge { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string IdentityKey { get; set; } = null!;

    public DateTime CreatedDate { get; set; }
}

/// <summary>
/// A printer the user connected to a bridge from the app: the bridge's desired printer list,
/// returned on every check-in. Only an interactive user creates one. Its LAN credentials are in
/// <see cref="BridgePrinterSecret"/>, sealed to the bridge's key; never in plaintext.
/// </summary>
public class BridgePrinter
{
    [Key]
    public long Id { get; set; }

    public long BridgeId { get; set; }
    [ForeignKey("BridgeId")]
    public Bridge Bridge { get; set; } = null!;

    /// <summary>The 3D Print Log printer the bridge logs this printer's prints to.</summary>
    public long PrinterId { get; set; }
    [ForeignKey("PrinterId")]
    public Printer Printer { get; set; } = null!;

    /// <summary>The adapter: one of <see cref="Connection.BridgeKinds"/>.</summary>
    [Required]
    [MaxLength(50)]
    public string Kind { get; set; } = null!;

    /// <summary>A private IP address or a local host name.</summary>
    [Required]
    [MaxLength(253)]
    public string Host { get; set; } = null!;

    public int? Port { get; set; }

    [MaxLength(64)]
    public string? Serial { get; set; }

    /// <summary>The discovered printer's identity, so the bridge can follow it to a new address.</summary>
    [MaxLength(200)]
    public string? IdentityKey { get; set; }

    /// <summary>The discovered printer's MAC addresses, space-separated.</summary>
    [MaxLength(200)]
    public string? Macs { get; set; }

    /// <summary>
    /// What the bridge last reported it can't use, space-separated <c>field:code</c> pairs
    /// (<c>access_code:undecryptable</c>). Codes: <c>SealedCredentials.IssueCodes</c>.
    /// </summary>
    [MaxLength(400)]
    public string? CredentialIssues { get; set; }

    /// <summary>
    /// Changes with every write of <see cref="CredentialIssues"/> or a sealed value, and is checked
    /// on each: a check-in that read the entry before an owner sealed a new value can't then
    /// overwrite the issue that seal cleared.
    /// </summary>
    [ConcurrencyCheck]
    public Guid IssuesStamp { get; set; }

    public DateTime CreatedDate { get; set; }

    /// <summary>
    /// When the user last moved it here from another bridge, UTC, or null. The bridge has watched
    /// the printer since this (or <see cref="CreatedDate"/>): a job that started before it belongs
    /// to the bridge that watched it then.
    /// </summary>
    public DateTime? MovedAt { get; set; }

    public ICollection<BridgePrinterSecret>? Secrets { get; set; }
}

/// <summary>
/// One LAN credential of a desired printer, sealed in the browser to the bridge's public key
/// (docs/bridge-credential-sealing.md). The API stores and forwards the ciphertext and cannot
/// read it. Only the owning bridge's device endpoints ever return <see cref="SealedValue"/>.
/// A row whose <see cref="KeyFingerprint"/> is not the bridge's current one is stale: the bridge
/// can't open it, so it isn't sent, and the owner is asked to seal it again.
/// </summary>
public class BridgePrinterSecret
{
    [Key]
    public long Id { get; set; }

    public long BridgePrinterId { get; set; }
    [ForeignKey("BridgePrinterId")]
    public BridgePrinter BridgePrinter { get; set; } = null!;

    /// <summary>The adapter's field: <c>access_code</c>, <c>password</c>, <c>username</c>, <c>api_key</c>, <c>moonraker_api_key</c>.</summary>
    [Required]
    [MaxLength(40)]
    public string Field { get; set; } = null!;

    /// <summary><c>v1.</c> and base64url of the sealed box. Never logged, never in telemetry.</summary>
    [Required]
    [MaxLength(512)]
    public string SealedValue { get; set; } = null!;

    /// <summary>The fingerprint of the bridge key it was sealed to.</summary>
    [Required]
    [MaxLength(64)]
    public string KeyFingerprint { get; set; } = null!;

    public DateTime UpdatedDate { get; set; }
}

/// <summary>
/// A job this bridge reported for a printer another bridge is bound to (printlog-bridge#103,
/// story 15a), by the bridge's external pair, so the bridge sending it again (when it ends, or as
/// a claim) isn't counted again in <see cref="Bridge.SharedJobCount"/>. Kept for
/// <c>BridgePrintGuard.SharedJobRetention</c>; each new one prunes older rows of its bridge.
/// </summary>
public class BridgeSharedJob
{
    [Key]
    public long Id { get; set; }

    public long BridgeId { get; set; }
    [ForeignKey("BridgeId")]
    public Bridge Bridge { get; set; } = null!;

    [Required]
    [MaxLength(50)]
    public string ExternalSource { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string ExternalId { get; set; } = null!;

    /// <summary>When the job was first reported, UTC.</summary>
    public DateTime ReportedAt { get; set; }
}
