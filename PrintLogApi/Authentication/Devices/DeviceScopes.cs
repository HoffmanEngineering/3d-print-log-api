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

    /// <summary>Loading and unloading spools, including the whole-printer <c>PUT</c> the bridge falls back to.</summary>
    public const string PrintersSlots = "printers:slots";

    public const string FilamentsRead = "filaments:read";

    /// <summary>Print attachments (G-code); the bridge asks for it only for Pro users.</summary>
    public const string FilesWrite = "files:write";

    /// <summary>Registering a connection and its heartbeat (#149).</summary>
    public const string ConnectionsWrite = "connections:write";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        PrintsRead, PrintsWrite, PrintersRead, PrintersSlots, FilamentsRead, FilesWrite, ConnectionsWrite,
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

    /// <summary>The stored form: space-separated.</summary>
    public static string Join(IEnumerable<string> scopes) => string.Join(' ', scopes);

    public static IReadOnlyList<string> Split(string stored) => FromScopeClaim(stored);
}
