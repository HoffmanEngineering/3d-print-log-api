using System.Globalization;
using System.Security.Claims;

namespace PrintLogApi.Authentication.Devices;

/// <summary>The id of the device grant a request authenticated with (#148). Not a secret.</summary>
public static class DeviceGrantClaim
{
    public const string Type = "device_grant_id";

    public static long? Get(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirst(Type)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}
