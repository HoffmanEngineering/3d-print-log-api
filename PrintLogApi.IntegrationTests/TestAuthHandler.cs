using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintLogApi.Users;

namespace PrintLogApi.IntegrationTests
{
    /// <summary>
    /// Test authentication handler that creates an authenticated user for integration tests.
    /// Looks up the user by OAuth ID to get the internal user ID, mimicking the real ClaimsTransformer.
    /// </summary>
    public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string AuthenticationScheme = "TestScheme";
        public const string TestUserIdHeader = "X-Test-User-Id";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // Check if the test user header is present
            if (!Request.Headers.TryGetValue(TestUserIdHeader, out var userIdHeader))
            {
                return AuthenticateResult.NoResult();
            }

            var oauthUserId = userIdHeader.ToString();

            // Look up the internal user ID from the OAuth ID, just like ClaimsTransformer does
            var userService = Context.RequestServices.GetRequiredService<IUserService>();
            var localUserId = await userService.GetLocalUserIdByAuthUserId(oauthUserId);

            if (localUserId == 0)
            {
                return AuthenticateResult.Fail($"User not found for OAuth ID: {oauthUserId}");
            }

            // Create claims for the test user
            // Include both Upn (OAuth ID) and NameIdentifier (internal ID)
            var claims = new[]
            {
                new Claim(ClaimTypes.Upn, oauthUserId),
                new Claim(ClaimTypes.NameIdentifier, localUserId.ToString()),
                new Claim(ClaimTypes.Name, "Test User"),
                new Claim("sub", oauthUserId),
            };

            var identity = new ClaimsIdentity(claims, AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, AuthenticationScheme);

            return AuthenticateResult.Success(ticket);
        }
    }
}
