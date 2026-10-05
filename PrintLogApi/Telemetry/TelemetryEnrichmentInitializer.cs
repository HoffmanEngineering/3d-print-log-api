using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using PrintLogApi.Extensions;

namespace PrintLogApi.Telemetry;

/// <summary>
/// Stamps every telemetry item produced during a request with who made it and how they
/// authenticated, so the Usage workbook can count users and split traffic by integration.
/// </summary>
/// <remarks>
/// <para>
/// <c>AuthenticatedUserId</c> is the internal integer user id, never the Auth0 subject or an
/// email, so nothing enters telemetry that identifies a person outside this database.
/// </para>
/// <para>
/// Requests are tracked at pipeline exit and dependencies and events at their own
/// <c>Track()</c>, both after authentication has run, so the principal is populated by the
/// time this initializer sees the item. The <c>ApiUser</c> role is what
/// <c>ApiKeyMiddleware</c> assigns; MCP is recognised by path because the SDK's bearer
/// scheme is only ever mapped there.
/// </para>
/// </remarks>
public sealed class TelemetryEnrichmentInitializer(IHttpContextAccessor httpContextAccessor) : ITelemetryInitializer
{
    public const string AuthMethodProperty = "authMethod";

    public const string ApiKey = "apiKey";
    public const string Mcp = "mcp";
    public const string Jwt = "jwt";
    public const string Anonymous = "anonymous";

    public void Initialize(ITelemetry telemetry)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return;
        }

        var user = httpContext.User;

        if (string.IsNullOrEmpty(telemetry.Context.User.AuthenticatedUserId)
            && user.GetUserId() is { } userId)
        {
            telemetry.Context.User.AuthenticatedUserId = userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (telemetry is ISupportProperties withProperties
            && !withProperties.Properties.ContainsKey(AuthMethodProperty))
        {
            withProperties.Properties[AuthMethodProperty] = ClassifyAuthMethod(httpContext);
        }
    }

    private static string ClassifyAuthMethod(HttpContext httpContext)
    {
        var user = httpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return Anonymous;
        }

        if (user.IsInRole("ApiUser"))
        {
            return ApiKey;
        }

        if (httpContext.Request.Path.StartsWithSegments("/mcp"))
        {
            return Mcp;
        }

        return Jwt;
    }
}
