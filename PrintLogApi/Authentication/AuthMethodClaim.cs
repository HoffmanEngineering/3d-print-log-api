using System.Security.Claims;

namespace PrintLogApi.Authentication;

/// <summary>
/// A typed marker for how a request authenticated. <c>ApiKeyMiddleware</c> adds it, so code that
/// cares (print provenance) never has to infer it from the generic "API" identity name.
/// </summary>
public static class AuthMethodClaim
{
    public const string Type = "auth_method";
    public const string ApiKey = "api_key";

    public static bool IsApiKey(ClaimsPrincipal user) => user.HasClaim(Type, ApiKey);
}
