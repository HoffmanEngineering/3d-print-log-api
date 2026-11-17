using System.Globalization;
using System.Security.Claims;

namespace PrintLogApi.Authentication.Devices;

/// <summary>The id of the device grant a request authenticated with (#148). Not a secret.</summary>
public static class DeviceGrantClaim
{
    public const string Type = "device_grant_id";

    /// <summary>
    /// Present ("true") when the request used a token a re-pair replaced, still inside its grace
    /// (api#287). Such a request works, but must not change what the owner is asked to trust.
    /// </summary>
    public const string SupersededType = "device_token_superseded";

    public static bool IsSuperseded(ClaimsPrincipal user) => user.HasClaim(SupersededType, "true");

    public static long? Get(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirst(Type)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}
