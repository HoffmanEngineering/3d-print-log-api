namespace PrintLogApi.Email;

/// <summary>
/// Custom claims the Auth0 post-login Action adds to the API access token
/// (docs/auth0/add-email-claims.js). Namespaced because Auth0 drops non-namespaced custom claims.
/// </summary>
public static class EmailClaims
{
    public const string Email = "https://3dprintlog.com/email";
    public const string EmailVerified = "https://3dprintlog.com/email_verified";
}
