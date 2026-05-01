using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Authentication.Handlers
{
    public class DevAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public DevAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Dev-User-Id", out var userIdValues)
                || string.IsNullOrWhiteSpace(userIdValues))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var userId = userIdValues.ToString().Trim();
            var claims = new[]
            {
                new Claim(ClaimTypes.Upn, $"dev|{userId}"),
            };

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
