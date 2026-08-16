using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Authentication.Handlers;

public class DevAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IConfiguration configuration;

    public DevAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        this.configuration = configuration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Dev-User-Id", out var userIdValues)
            || string.IsNullOrWhiteSpace(userIdValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var userId = userIdValues.ToString().Trim();

        // Match the issuer HasScopeHandler expects so the dev bypass satisfies the MCP scope
        // requirements without contacting Auth0. Both read and write scopes are granted so the
        // full MCP tool surface (McpRead and McpWrite) is exercisable under the dev bypass.
        var issuer = $"https://{configuration["Auth0:Domain"]}/";

        var claims = new[]
        {
            new Claim(ClaimTypes.Upn, $"dev|{userId}"),
            new Claim("scope", "read:printdata write:printdata", ClaimValueTypes.String, issuer),
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
