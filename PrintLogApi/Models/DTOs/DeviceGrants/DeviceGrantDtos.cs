namespace PrintLogApi.Models.DTOs.DeviceGrants;

/// <summary>The body of <c>POST /api/device-grants</c>.</summary>
public class PairDeviceDto
{
    /// <summary>
    /// A UUID the device generates once and keeps. Pairing again with the same id replaces that
    /// device's token instead of adding a second device.
    /// </summary>
    public Guid? DeviceId { get; set; }

    /// <summary>What to call the device in the list of paired devices, up to 100 characters ("printlog-bridge on voron").</summary>
    public string? Name { get; set; }
}

/// <summary>
/// A paired device, as the user's list shows it. Never carries the token.
/// </summary>
/// <param name="Id">The grant id; revoke with <c>DELETE /api/device-grants/{id}</c>.</param>
/// <param name="DeviceId">The device's own id.</param>
/// <param name="Name">What the device calls itself.</param>
/// <param name="ClientId">The Auth0 application it paired through.</param>
/// <param name="Scopes">What it may do.</param>
/// <param name="CreatedAt">First paired.</param>
/// <param name="PairedAt">Its current token was issued (first pairing or latest re-pair).</param>
/// <param name="LastUsedAt">Last request, to within five minutes; null until first used.</param>
public sealed record DeviceGrantDto(
    long Id,
    Guid DeviceId,
    string Name,
    string? ClientId,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset PairedAt,
    DateTimeOffset? LastUsedAt);

/// <summary>
/// The answer to pairing: the grant and its device token. The token is shown this once and
/// stored only as a hash, so a device that loses it pairs again.
/// </summary>
public sealed record PairedDeviceDto(
    long Id,
    Guid DeviceId,
    string Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    string Token,
    string TokenType);

/// <summary>
/// What a device needs to start the Auth0 device authorization flow (RFC 8628): where to ask for
/// a code, where to poll, and with which client, audience and scopes.
/// </summary>
public sealed record DevicePairingConfigDto(
    string Issuer,
    string DeviceAuthorizationEndpoint,
    string TokenEndpoint,
    string ClientId,
    string Audience,
    IReadOnlyList<string> Scopes);
