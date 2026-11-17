namespace PrintLogApi.Authentication.Devices;

/// <summary>
/// The <c>DevicePairing</c> configuration section (#148): the Auth0 Native application devices
/// run the device flow with, and the API (audience) it issues their pairing tokens for. Blank
/// <see cref="ClientId"/> or <see cref="Audience"/> switches pairing off: the discovery endpoint
/// answers 503 and the pairing policy refuses every token. Device tokens already issued keep
/// working, since they are the API's own.
/// </summary>
public sealed class DevicePairingOptions
{
    public const string Section = "DevicePairing";

    /// <summary>
    /// The Auth0 domain the device flow runs on, and the issuer of its tokens. Blank means
    /// <c>Auth0:Domain</c>. Set it to the custom domain (auth.3dprintlog.com) once there is one:
    /// a token's <c>iss</c> is the domain it was requested from, so the bridge and this setting
    /// must agree.
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>The Auth0 Native application's client id. Public; the bridge needs it to start the flow.</summary>
    public string? ClientId { get; set; }

    /// <summary>The Auth0 API identifier the device flow requests (not the web app's audience, not the MCP one).</summary>
    public string? Audience { get; set; }

    /// <summary>
    /// How old a pairing token may be, by its <c>iat</c>, whatever the API's token lifetime in
    /// Auth0 says. Pairing happens right after the flow completes, so this only bounds how long
    /// a leaked pairing token stays exchangeable.
    /// </summary>
    public int MaxPairingTokenAgeMinutes { get; set; } = 10;

    /// <summary>Paired devices per user. Re-pairing an existing device does not count.</summary>
    public int MaxGrantsPerUser { get; set; } = 25;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(Audience) && !string.IsNullOrWhiteSpace(Domain);

    /// <summary>The issuer URL, <c>https://{domain}/</c>, as Auth0 writes it in <c>iss</c>.</summary>
    public string Issuer => $"https://{Domain}/";
}
