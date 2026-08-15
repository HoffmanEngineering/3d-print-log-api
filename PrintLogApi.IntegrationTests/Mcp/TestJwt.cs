using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// Mints locally signed JWTs for integration tests so the real JwtBearer / McpBearer
    /// schemes can be exercised without contacting Auth0. Issuer/audience/scope/subject/expiry
    /// are all caller-controlled to prove the dedicated MCP resource boundary.
    /// </summary>
    public static class TestJwt
    {
        /// <summary>Must match <c>https://{Auth0:Domain}/</c> for appsettings.IntegrationTesting.json.</summary>
        public const string Issuer = "https://test.auth0.com/";

        /// <summary>Default app API audience (Auth0:ApiIdentifier in IntegrationTesting).</summary>
        public const string ApiAudience = "https://test.api";

        /// <summary>Dedicated MCP audience (Auth0:McpIdentifier in IntegrationTesting).</summary>
        public const string McpAudience = "https://test.mcp";

        /// <summary>Stable signing key shared by the token minter and the JwtBearer validators.</summary>
        public static readonly RsaSecurityKey SigningKey =
            new(RSA.Create(2048)) { KeyId = "integration-test-key" };

        public static string Create(
            string audience,
            string? subject = "auth0|mcp-user",
            IEnumerable<string>? scopes = null,
            string issuer = Issuer,
            DateTime? expires = null,
            DateTime? notBefore = null)
        {
            var handler = new JsonWebTokenHandler();
            var claims = new List<Claim>();
            if (subject != null)
            {
                claims.Add(new Claim(JwtRegisteredClaimNames.Sub, subject));
            }
            if (scopes != null)
            {
                claims.Add(new Claim("scope", string.Join(' ', scopes)));
            }

            var now = DateTime.UtcNow;
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Subject = new ClaimsIdentity(claims),
                IssuedAt = now,
                NotBefore = notBefore ?? now,
                Expires = expires ?? now.AddHours(1),
                SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
            };

            return handler.CreateToken(descriptor);
        }
    }
}
