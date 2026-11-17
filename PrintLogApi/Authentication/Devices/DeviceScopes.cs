namespace PrintLogApi.Authentication.Devices;

/// <summary>
/// The scopes a device grant can hold (#148). They are requested in the Auth0 device flow, on the
/// device audience, and copied onto the grant at pairing; the API never sees them on a request
/// again, it reads them off the grant row. Each allowed endpoint names one with
/// <see cref="DeviceAccessAttribute"/>.
/// </summary>
public static class DeviceScopes
{
    public const string PrintsRead = "prints:read";
    public const string PrintsWrite = "prints:write";
    public const string PrintersRead = "printers:read";

    /// <summary>Adding a printer, which the bridge offers while pairing ("create new").</summary>
    public const string PrintersCreate = "printers:create";

    /// <summary>Loading and unloading spools, including the whole-printer <c>PUT</c> the bridge falls back to.</summary>
    public const string PrintersSlots = "printers:slots";

    public const string FilamentsRead = "filaments:read";

    /// <summary>Print attachments (G-code); the bridge asks for it only for Pro users.</summary>
    public const string FilesWrite = "files:write";

    /// <summary>Registering a connection and its heartbeat (#149).</summary>
    public const string ConnectionsWrite = "connections:write";

    /// <summary>
    /// The bridge's own check-in (printlog-bridge#100): it reports itself and the printers it
    /// found on its network, and reads the printers the user chose for it. Never adds one.
    /// </summary>
    public const string BridgeSync = "bridge:sync";

    /// <summary>
    /// What a token a re-pair replaced may still do during its grace (api#287): log prints and
    /// send heartbeats. Every other device endpoint answers it 403 <c>device_token_superseded</c>
    /// (<c>DeviceTokenMiddleware</c>). Widening this is a security decision, like the allow-list.
    /// </summary>
    public static readonly IReadOnlySet<string> DuringRepairGrace = new HashSet<string>(StringComparer.Ordinal) { PrintsWrite, ConnectionsWrite };

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        PrintsRead, PrintsWrite, PrintersRead, PrintersCreate, PrintersSlots, FilamentsRead, FilesWrite, ConnectionsWrite, BridgeSync,
    };

    /// <summary>
    /// The device scopes in an OAuth <c>scope</c> value, sorted and de-duplicated. Anything else
    /// (<c>openid</c>, <c>offline_access</c>, a scope of another API) is dropped, so a grant can
    /// only ever hold scopes this class defines.
    /// </summary>
    public static IReadOnlyList<string> FromScopeClaim(string? scope) =>
        (scope ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(All.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// What each scope lets a device do, in the order they are listed, worded as the web app's
    /// Connected Agents list words them (3d-print-log-ui <c>device-scopes.ts</c>).
    /// </summary>
    private static readonly (string Scope, string Label)[] Labels =
    [
        (PrintsWrite, "log prints"),
        (PrintsRead, "see prints"),
        (PrintersRead, "see printers"),
        (PrintersCreate, "add printers"),
        (PrintersSlots, "update printer slots"),
        (FilamentsRead, "see materials"),
        (FilesWrite, "upload print files"),
        (ConnectionsWrite, "send heartbeats"),
        (BridgeSync, "report printers it finds on your network"),
    ];

    /// <summary>"Log prints, update printer slots, send heartbeats" for a grant's scopes.</summary>
    public static string Describe(IEnumerable<string> scopes)
    {
        var held = scopes.ToHashSet(StringComparer.Ordinal);
        var parts = Labels.Where(l => held.Contains(l.Scope)).Select(l => l.Label)
            .Concat(held.Where(s => !Labels.Any(l => l.Scope == s)).Order(StringComparer.Ordinal))
            .ToList();
        if (parts.Count == 0)
        {
            return "No permissions";
        }

        var sentence = string.Join(", ", parts);
        return char.ToUpperInvariant(sentence[0]) + sentence[1..];
    }

    /// <summary>The stored form: space-separated.</summary>
    public static string Join(IEnumerable<string> scopes) => string.Join(' ', scopes);

    public static IReadOnlyList<string> Split(string stored) => FromScopeClaim(stored);
}
