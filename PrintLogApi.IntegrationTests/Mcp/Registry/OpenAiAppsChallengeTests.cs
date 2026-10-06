using System.Net;
using PrintLogApi.Mcp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp.Registry;

/// <summary>
/// OpenAI fetches the verification token anonymously, from the API's origin, as plain text. Each
/// of those is a way the endpoint could quietly fail the ChatGPT directory's domain check.
/// </summary>
public class OpenAiAppsChallengeTests
{
    private const string Token = "test-challenge-token-0123456789";

    public sealed class ConfiguredFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Surrounding whitespace is what a pasted App Service setting tends to carry.
                    [OpenAiAppsChallenge.ConfigurationKey] = $"  {Token}\n",
                }));
            base.ConfigureWebHost(builder);
        }
    }

    public class WhenConfigured : IClassFixture<ConfiguredFactory>
    {
        private readonly ConfiguredFactory _factory;
        public WhenConfigured(ConfiguredFactory factory) => _factory = factory;

        [Fact]
        public async Task ServesTheTrimmedTokenAsPlainText_WithoutAuthentication()
        {
            var client = _factory.CreateClient();

            var response = await client.GetAsync(OpenAiAppsChallenge.Path, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(Token, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    public class WhenNotConfigured : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;
        public WhenNotConfigured(CustomWebApplicationFactory factory) => _factory = factory;

        [Fact]
        public async Task Returns404()
        {
            var client = _factory.CreateClient();

            var response = await client.GetAsync(OpenAiAppsChallenge.Path, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
