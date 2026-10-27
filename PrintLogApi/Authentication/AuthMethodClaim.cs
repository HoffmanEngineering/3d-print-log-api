using System.Security.Claims;

namespace PrintLogApi.Authentication;

/// <summary>
/// A typed marker for how a request authenticated. <c>ApiKeyMiddleware</c> and
/// <c>DeviceTokenMiddleware</c> add it, so code that cares (print provenance) never has to infer
/// it from the generic "API" identity name.
/// </summary>
public static class AuthMethodClaim
{
    public const string Type = "auth_method";
    public const string ApiKey = "api_key";
    public const string Device = "device";

    public static bool IsApiKey(ClaimsPrincipal user) => user.HasClaim(Type, ApiKey);

    public static bool IsDevice(ClaimsPrincipal user) => user.HasClaim(Type, Device);
}
