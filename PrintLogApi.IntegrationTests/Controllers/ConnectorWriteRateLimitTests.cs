using System.Net;
using System.Text.Json;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Covers the per-credential budget on connector writes (#151): a token bucket on every
/// POST/PUT/PATCH/DELETE made with a connector credential, keyed on that credential rather than
/// on the user. Today the only connector credential is a personal API key; device tokens (#148)
/// are meant to plug into the same partition.
/// The connection heartbeat is exempt (#200).
///
/// The suite runs with the budget disabled; these tests re-enable it with a tiny burst and a refill
/// of one request per minute, so nothing refills while a test runs and the window is predictable.
/// </summary>
public class ConnectorWriteRateLimitTests
{
    public const int Burst = 3;

    public sealed class LowConnectorBudgetFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Api:ConnectorWrites:PerMinute"] = "1",
                    ["Api:ConnectorWrites:Burst"] = Burst.ToString(),
                }));
            base.ConfigureWebHost(builder);
        }
    }

    /// <summary>
    /// A rate that does not divide a minute evenly: one token every 60/7 = 8.57 seconds, with a
    /// burst of one, so the second write is rejected with a fractional wait.
    /// </summary>
    public sealed class FractionalRefillFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Api:ConnectorWrites:PerMinute"] = "7",
                    ["Api:ConnectorWrites:Burst"] = "1",
                }));
            base.ConfigureWebHost(builder);
        }
    }

    private static string AddUser(CustomWebApplicationFactory factory, string oauthUserId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        context.Users.Add(new PrintLogApi.Models.User
        {
            OAuthUserId = oauthUserId,
            ViewStatus = PrintLogApi.Models.User.ProfileViewStatus.Public,
        });
        context.SaveChanges();

        return oauthUserId;
    }

    private static async Task<string> CreateApiKey(HttpClient client, string oauthUserId)
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys");
        create.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthUserId);
        create.Content = JsonContent.Create(new { Description = "Connector rate limit test" });
        var created = await client.SendAsync(create, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        return (await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken))
            .GetProperty("publicKey").GetString()!;
    }

    /// <summary>
    /// A counted write that needs nothing seeded and is a deterministic 2xx. Heartbeats used to
    /// serve here, but they are exempt from the budget (#200).
    /// </summary>
    private static HttpRequestMessage Write()
        => new(HttpMethod.Post, "/api/Projects")
        {
            Content = JsonContent.Create(new { Name = "Connector rate limit project" }),
        };

    /// <summary>A print write: what a backfill sends, and what the budget exists to bound.</summary>
    private static HttpRequestMessage PrintWrite()
        => new(HttpMethod.Post, "/api/Prints")
        {
            Content = JsonContent.Create(new { Title = "Backfilled print" }),
        };

    /// <summary>A connection heartbeat, exempt from the budget (#200).</summary>
    private static HttpRequestMessage Heartbeat(string instanceId)
        => new(HttpMethod.Put, $"/api/Connections/{instanceId}")
        {
            Content = JsonContent.Create(new { Kind = "moonraker", DisplayName = "Rate limit bridge" }),
        };

    private static HttpRequestMessage WithKey(HttpRequestMessage request, string key)
    {
        request.Headers.Add("X-Api-Key", key);
        return request;
    }

    private static HttpRequestMessage AsUser(HttpRequestMessage request, string oauthUserId)
    {
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthUserId);
        return request;
    }

    // One fixture per class: the budget is per credential and a test mints its own keys, but the
    // refill is a minute long, so isolating classes keeps any shared state from leaking.
    public class Budget(LowConnectorBudgetFactory factory) : IClassFixture<LowConnectorBudgetFactory>
    {
        [Fact]
        public async Task WritesBeyondTheBurst_Return429_WithRetryAfterInSeconds()
        {
            var client = factory.CreateClient();
            var user = AddUser(factory, "auth0|connector-limit-exhaust");
            var key = await CreateApiKey(client, user);

            var responses = new List<HttpResponseMessage>();
            for (var i = 0; i < Burst + 1; i++)
            {
                responses.Add(await client.SendAsync(WithKey(Write(), key), TestContext.Current.CancellationToken));
            }

            // Success, not merely "not 429": a mistyped route would 404 or 405 without ever
            // reaching the limiter.
            Assert.All(responses.Take(Burst), r => Assert.True(r.IsSuccessStatusCode, $"expected 2xx, got {(int)r.StatusCode}"));

            var limited = responses[Burst];
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

            // Retry-After is delta-seconds: one token refills per minute at this configuration,
            // and a 0 would tell the client to hammer straight back.
            var retryAfter = limited.Headers.RetryAfter;
            Assert.NotNull(retryAfter);
            Assert.NotNull(retryAfter.Delta);
            Assert.InRange(retryAfter.Delta.Value.TotalSeconds, 1, 60);
        }

        [Fact]
        public async Task Budgets_ArePerCredential_NotPerUser()
        {
            var client = factory.CreateClient();
            var user = AddUser(factory, "auth0|connector-limit-two-keys");
            var first = await CreateApiKey(client, user);
            var second = await CreateApiKey(client, user);

            for (var i = 0; i < Burst + 1; i++)
            {
                await client.SendAsync(WithKey(Write(), first), TestContext.Current.CancellationToken);
            }

            // Two bridges of one user each have their own budget: a backfill on one must not
            // stall the writes of another.
            var other = await client.SendAsync(WithKey(Write(), second), TestContext.Current.CancellationToken);
            Assert.True(other.IsSuccessStatusCode, $"expected 2xx, got {(int)other.StatusCode}");
        }

        [Fact]
        public async Task WebAppWrites_AreNotAffected_EvenWhenTheUsersKeyIsExhausted()
        {
            var client = factory.CreateClient();
            var user = AddUser(factory, "auth0|connector-limit-web-app");
            var key = await CreateApiKey(client, user);

            for (var i = 0; i < Burst + 1; i++)
            {
                await client.SendAsync(WithKey(Write(), key), TestContext.Current.CancellationToken);
            }

            // The same user, signed in to the web app, writes well past the connector burst.
            for (var i = 0; i < Burst * 2; i++)
            {
                var response = await client.SendAsync(AsUser(Write(), user), TestContext.Current.CancellationToken);
                Assert.True(response.IsSuccessStatusCode, $"expected 2xx, got {(int)response.StatusCode}");
            }
        }

        [Fact]
        public async Task Heartbeats_AreExempt_WhileTheKeysPrintWritesAreStillLimited()
        {
            var client = factory.CreateClient();
            var user = AddUser(factory, "auth0|connector-limit-heartbeat");
            var key = await CreateApiKey(client, user);

            for (var i = 0; i < Burst; i++)
            {
                var response = await client.SendAsync(WithKey(Write(), key), TestContext.Current.CancellationToken);
                Assert.True(response.IsSuccessStatusCode, $"expected 2xx, got {(int)response.StatusCode}");
            }

            // A backfill has drained the key's bucket. Its heartbeats must still land, or three
            // missed ones mark the bridge stale and the Moonraker notifier starts duplicating its
            // prints. Several, so a heartbeat that spent a token would run out within the test.
            for (var i = 0; i < Burst + 1; i++)
            {
                var heartbeat = await client.SendAsync(WithKey(Heartbeat("bridge-heartbeat"), key), TestContext.Current.CancellationToken);
                Assert.True(heartbeat.IsSuccessStatusCode, $"expected 2xx, got {(int)heartbeat.StatusCode}");
            }

            // The exemption is the heartbeat's alone: the same key's print writes are still held.
            var print = await client.SendAsync(WithKey(PrintWrite(), key), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.TooManyRequests, print.StatusCode);
        }

        [Fact]
        public async Task ConnectorReads_AreNotCounted()
        {
            var client = factory.CreateClient();
            var user = AddUser(factory, "auth0|connector-limit-reads");
            var key = await CreateApiKey(client, user);

            for (var i = 0; i < Burst * 2; i++)
            {
                var response = await client.SendAsync(WithKey(new HttpRequestMessage(HttpMethod.Get, "/api/Connections"), key), TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            // Reads spent nothing, so the whole write burst is still there.
            for (var i = 0; i < Burst; i++)
            {
                var response = await client.SendAsync(WithKey(Write(), key), TestContext.Current.CancellationToken);
                Assert.True(response.IsSuccessStatusCode, $"expected 2xx, got {(int)response.StatusCode}");
            }
        }
    }

    public class RetryAfterRounding(FractionalRefillFactory factory) : IClassFixture<FractionalRefillFactory>
    {
        [Fact]
        public async Task RetryAfter_RoundsAFractionalWaitUp()
        {
            var client = factory.CreateClient();
            var user = AddUser(factory, "auth0|connector-limit-rounding");
            var key = await CreateApiKey(client, user);

            var first = await client.SendAsync(WithKey(Write(), key), TestContext.Current.CancellationToken);
            Assert.True(first.IsSuccessStatusCode, $"expected 2xx, got {(int)first.StatusCode}");

            var limited = await client.SendAsync(WithKey(Write(), key), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

            // 8.57 seconds must be advertised as 9. Truncating to 8 sends a well-behaved client
            // back before a token exists, and it is rejected again.
            Assert.Equal(TimeSpan.FromSeconds(9), limited.Headers.RetryAfter?.Delta);
        }
    }
}
