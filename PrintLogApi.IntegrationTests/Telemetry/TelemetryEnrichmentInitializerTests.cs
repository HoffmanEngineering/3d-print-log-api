using System.Security.Claims;
using Microsoft.ApplicationInsights.DataContracts;
using PrintLogApi.Telemetry;
using Xunit;

namespace PrintLogApi.IntegrationTests.Telemetry;

public class TelemetryEnrichmentInitializerTests
{
    private static HttpContext Context(ClaimsPrincipal? user = null, string path = "/api/prints")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (user != null)
        {
            context.User = user;
        }
        return context;
    }

    private static ClaimsPrincipal Authenticated(long userId, string authType = "Bearer", params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authType));
    }

    private static TelemetryEnrichmentInitializer Initializer(HttpContext? context)
        => new(new HttpContextAccessor { HttpContext = context });

    [Fact]
    public void Anonymous_request_is_classified_anonymous_with_no_user()
    {
        var telemetry = new RequestTelemetry();

        Initializer(Context()).Initialize(telemetry);

        Assert.Equal(TelemetryEnrichmentInitializer.Anonymous, telemetry.Properties[TelemetryEnrichmentInitializer.AuthMethodProperty]);
        Assert.True(string.IsNullOrEmpty(telemetry.Context.User.AuthenticatedUserId));
    }

    [Fact]
    public void Jwt_request_stamps_internal_user_id()
    {
        var telemetry = new RequestTelemetry();

        Initializer(Context(Authenticated(42))).Initialize(telemetry);

        Assert.Equal(TelemetryEnrichmentInitializer.Jwt, telemetry.Properties[TelemetryEnrichmentInitializer.AuthMethodProperty]);
        Assert.Equal("42", telemetry.Context.User.AuthenticatedUserId);
    }

    [Fact]
    public void ApiUser_role_is_classified_apiKey_even_on_mcp_path()
    {
        // The role is what ApiKeyMiddleware assigns; it outranks the path check because an
        // API key can in principle be presented anywhere.
        var telemetry = new DependencyTelemetry();

        Initializer(Context(Authenticated(7, "API", "ApiUser"), "/mcp")).Initialize(telemetry);

        Assert.Equal(TelemetryEnrichmentInitializer.ApiKey, telemetry.Properties[TelemetryEnrichmentInitializer.AuthMethodProperty]);
        Assert.Equal("7", telemetry.Context.User.AuthenticatedUserId);
    }

    [Fact]
    public void Authenticated_request_on_mcp_path_is_classified_mcp()
    {
        var telemetry = new EventTelemetry("Mcp_ToolCalled");

        Initializer(Context(Authenticated(9, "McpBearer"), "/mcp")).Initialize(telemetry);

        Assert.Equal(TelemetryEnrichmentInitializer.Mcp, telemetry.Properties[TelemetryEnrichmentInitializer.AuthMethodProperty]);
    }

    [Fact]
    public void Unauthenticated_request_on_mcp_path_is_anonymous()
    {
        var telemetry = new RequestTelemetry();

        Initializer(Context(path: "/mcp")).Initialize(telemetry);

        Assert.Equal(TelemetryEnrichmentInitializer.Anonymous, telemetry.Properties[TelemetryEnrichmentInitializer.AuthMethodProperty]);
    }

    [Fact]
    public void Does_not_overwrite_values_already_set()
    {
        var telemetry = new EventTelemetry("x");
        telemetry.Properties[TelemetryEnrichmentInitializer.AuthMethodProperty] = "custom";
        telemetry.Context.User.AuthenticatedUserId = "pre-set";

        Initializer(Context(Authenticated(42))).Initialize(telemetry);

        Assert.Equal("custom", telemetry.Properties[TelemetryEnrichmentInitializer.AuthMethodProperty]);
        Assert.Equal("pre-set", telemetry.Context.User.AuthenticatedUserId);
    }

    [Fact]
    public void Outside_a_request_leaves_telemetry_untouched()
    {
        var telemetry = new EventTelemetry("DeletePendingDeactivatedUsers");

        Initializer(null).Initialize(telemetry);

        Assert.False(telemetry.Properties.ContainsKey(TelemetryEnrichmentInitializer.AuthMethodProperty));
        Assert.True(string.IsNullOrEmpty(telemetry.Context.User.AuthenticatedUserId));
    }
}
