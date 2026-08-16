using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers
{
    /// <summary>
    /// Covers the "api" rate limiting policy applied to the REST controllers.
    ///
    /// The integration suite as a whole runs with both budgets disabled (0 in
    /// appsettings.IntegrationTesting.json) so unrelated tests never trip the limiter; these tests
    /// re-enable it at a deliberately tiny value through their own factories.
    /// </summary>
    public class ApiRateLimitTests
    {
        public const int Limit = 3;

        /// <summary>A factory with a tiny per-user REST budget and anonymous limiting left off.</summary>
        public sealed class LowUserLimitFactory : CustomWebApplicationFactory
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Api:RateLimitPerMinute"] = Limit.ToString(),
                        ["Api:AnonymousRateLimitPerMinute"] = "0",
                    }));
                base.ConfigureWebHost(builder);
            }
        }

        /// <summary>
        /// A factory where the general per-user budget is tiny but the media budget is roomy —
        /// the shape of the production config, exaggerated so it can be exercised in a few calls.
        /// </summary>
        public sealed class LowGeneralHighMediaFactory : CustomWebApplicationFactory
        {
            public const int MediaLimit = 10;

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Api:RateLimitPerMinute"] = Limit.ToString(),
                        ["Api:AnonymousRateLimitPerMinute"] = Limit.ToString(),
                        ["Api:MediaRateLimitPerMinute"] = MediaLimit.ToString(),
                    }));
                base.ConfigureWebHost(builder);
            }
        }

        /// <summary>A factory with a tiny allowance for rejected API keys.</summary>
        public sealed class LowInvalidKeyLimitFactory : CustomWebApplicationFactory
        {
            public const int AttemptLimit = 3;

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Api:InvalidApiKeyAttemptsPerMinute"] = AttemptLimit.ToString(),
                    }));
                base.ConfigureWebHost(builder);
            }
        }

        /// <summary>A factory with a tiny anonymous budget and per-user limiting left off.</summary>
        public sealed class LowAnonymousLimitFactory : CustomWebApplicationFactory
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Api:RateLimitPerMinute"] = "0",
                        ["Api:AnonymousRateLimitPerMinute"] = Limit.ToString(),
                    }));
                base.ConfigureWebHost(builder);
            }
        }

        private static HttpRequestMessage Authenticated(string userId)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Printers/summary");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, userId);
            return request;
        }

        /// <summary>
        /// Adds a second user to a factory's database. TestAuthHandler rejects an unknown OAuth id
        /// with a 401, and a 401 never reaches the limiter — so proving the partition is per-user
        /// needs a real second user rather than an arbitrary header value.
        /// </summary>
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

        public class PerUserBudget : IClassFixture<LowUserLimitFactory>
        {
            private readonly LowUserLimitFactory _factory;

            public PerUserBudget(LowUserLimitFactory factory) => _factory = factory;

            [Fact]
            public async Task ExceedingBudget_Returns429_WithRetryAfter()
            {
                var client = _factory.CreateClient();

                // Every test in this class needs its own user. The budget window is a minute and
                // the fixture is shared, so two tests spending the same partition would leave
                // whichever ran second with nothing.
                var user = AddUser(_factory, "auth0|rate-limit-exhaust");

                var responses = new List<HttpResponseMessage>();
                for (var i = 0; i < Limit + 1; i++)
                {
                    responses.Add(await client.SendAsync(Authenticated(user)));
                }

                // Assert success, not merely "not 429": a mistyped route 405s, which would satisfy
                // a NotEqual check while never reaching the limiter at all.
                Assert.All(responses.Take(Limit), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

                var last = responses[Limit];
                Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
                Assert.True(last.Headers.RetryAfter != null, "429 response should include Retry-After");
            }

            [Fact]
            public async Task Budgets_ArePerUser()
            {
                var client = _factory.CreateClient();
                var spender = AddUser(_factory, "auth0|rate-limit-spender");
                var otherUserOAuthId = AddUser(_factory, "auth0|rate-limit-second-user");

                // Exhaust one user's budget.
                for (var i = 0; i < Limit + 1; i++)
                {
                    await client.SendAsync(Authenticated(spender));
                }

                // A different subject still has a full budget. Assert success rather than merely
                // "not 429" — a 401 or 404 would satisfy the weaker check without the partition
                // having held at all.
                var other = await client.SendAsync(Authenticated(otherUserOAuthId));
                Assert.Equal(HttpStatusCode.OK, other.StatusCode);
            }
        }

        // Anonymous requests all partition on the same loopback address, so unlike the per-user
        // tests these cannot be separated by choosing a different caller. Each one therefore gets
        // its own test class: xUnit builds a fresh fixture — and so a fresh limiter — per class.
        public class AnonymousBudget : IClassFixture<LowAnonymousLimitFactory>
        {
            private readonly LowAnonymousLimitFactory _factory;

            public AnonymousBudget(LowAnonymousLimitFactory factory) => _factory = factory;

            [Fact]
            public async Task UnauthenticatedTraffic_IsLimited()
            {
                var client = _factory.CreateClient();

                var responses = new List<HttpResponseMessage>();
                for (var i = 0; i < Limit + 1; i++)
                {
                    // A public endpoint, so the request is not rejected before reaching the limiter.
                    responses.Add(await client.GetAsync("/api/Prints/summary?userId=1"));
                }

                Assert.All(responses.Take(Limit), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
                Assert.Equal(HttpStatusCode.TooManyRequests, responses[Limit].StatusCode);
            }
        }

        public class AnonymousBudgetIsolation : IClassFixture<LowAnonymousLimitFactory>
        {
            private readonly LowAnonymousLimitFactory _factory;

            public AnonymousBudgetIsolation(LowAnonymousLimitFactory factory) => _factory = factory;

            [Fact]
            public async Task AuthenticatedTraffic_DoesNotConsumeTheAnonymousBudget()
            {
                var client = _factory.CreateClient();

                // Exhaust the anonymous budget.
                for (var i = 0; i < Limit + 1; i++)
                {
                    await client.GetAsync("/api/Prints/summary?userId=1");
                }

                // An authenticated caller partitions on its user id instead, so it is unaffected
                // even though it shares the (single, loopback) test client IP.
                var authenticated = await client.SendAsync(
                    Authenticated(IntegrationTestSeeder.TestUserOAuthId));
                Assert.NotEqual(HttpStatusCode.TooManyRequests, authenticated.StatusCode);
            }
        }

        /// <summary>
        /// Image endpoints are fanned out by the browser: a gallery page of 100 prints requests 100
        /// images in a burst, so they must not be counted against the same budget as the data calls
        /// on that page. These tests pin both halves of that — the larger ceiling, and the separate
        /// partition.
        /// </summary>
        public class MediaBudget : IClassFixture<LowGeneralHighMediaFactory>
        {
            private readonly LowGeneralHighMediaFactory _factory;

            public MediaBudget(LowGeneralHighMediaFactory factory) => _factory = factory;

            // Each test authenticates as its own user. Anonymous image requests would all share the
            // one loopback media partition, and these two tests together spend more than the media
            // budget holds.
            //
            // The image id deliberately does not resolve. A seeded image id would send the handler
            // to blob storage, which means Azurite on 127.0.0.1:10000 — present on a dev machine,
            // absent in CI, where these tests failed with connection refused. Routing and the rate
            // limiter both run before the handler, so a 404 charges exactly the same budget and the
            // test becomes hermetic. Matches the existing not-found image tests in
            // PrintsControllerTests.
            private static HttpRequestMessage Image(string user)
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get, $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/image/999999");
                request.Headers.Add(TestAuthHandler.TestUserIdHeader, user);
                return request;
            }

            [Fact]
            public async Task ImageRequests_GetTheLargerMediaBudget()
            {
                var client = _factory.CreateClient();
                var user = AddUser(_factory, "auth0|rate-limit-media-exhaust");

                // Well past the general budget: this is the burst that a single uncached gallery
                // page produces, and it must survive.
                for (var i = 0; i < LowGeneralHighMediaFactory.MediaLimit; i++)
                {
                    var response = await client.SendAsync(Image(user));
                    Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
                }

                // The media budget is still a budget.
                var exhausted = await client.SendAsync(Image(user));
                Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);
            }

            [Fact]
            public async Task ProjectImages_AlsoGetTheMediaBudget()
            {
                var client = _factory.CreateClient();
                var user = AddUser(_factory, "auth0|rate-limit-project-media");

                // The project image endpoint carries [MediaEndpoint] too. It is a separate action
                // on a separate controller, so the annotation has to be verified separately —
                // covering only the print image endpoint would not catch a missing one here. The
                // project id need not exist: routing and the limiter both run before the handler,
                // so a 404 still proves which budget was charged.
                for (var i = 0; i < Limit * 2; i++)
                {
                    var request = new HttpRequestMessage(
                        HttpMethod.Get, $"/api/Projects/{System.Guid.NewGuid()}/images/1");
                    request.Headers.Add(TestAuthHandler.TestUserIdHeader, user);

                    var response = await client.SendAsync(request);
                    Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
                }
            }

            [Fact]
            public async Task ImageBurst_DoesNotSpendTheGeneralBudget()
            {
                var client = _factory.CreateClient();
                var user = AddUser(_factory, "auth0|rate-limit-media-partition");

                // A burst of images large enough to have blown the general budget several times.
                for (var i = 0; i < Limit * 2; i++)
                {
                    await client.SendAsync(Image(user));
                }

                // The data calls on the same page are untouched, because media partitions apart.
                var data = await client.SendAsync(Authenticated(user));
                Assert.Equal(HttpStatusCode.OK, data.StatusCode);
            }
        }

        /// <summary>
        /// A rejected API key never reaches the "api" policy — ApiKeyMiddleware short-circuits with
        /// a 401 long before UseRateLimiter — so key guessing is throttled by that middleware's own
        /// per-address guard. Without it, every guess reaches the database lookup for free.
        /// </summary>
        public class InvalidApiKeyThrottling : IClassFixture<LowInvalidKeyLimitFactory>
        {
            private readonly LowInvalidKeyLimitFactory _factory;

            public InvalidApiKeyThrottling(LowInvalidKeyLimitFactory factory) => _factory = factory;

            private static HttpRequestMessage WithKey(string key)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/api/UserApiKeys");
                request.Headers.Add("X-Api-Key", key);
                return request;
            }

            [Fact]
            public async Task RepeatedInvalidKeys_AreEventuallyThrottled()
            {
                var client = _factory.CreateClient();

                var responses = new List<HttpResponseMessage>();
                for (var i = 0; i < LowInvalidKeyLimitFactory.AttemptLimit + 1; i++)
                {
                    responses.Add(await client.SendAsync(WithKey("INVALIDKEY00000000000000000000000")));
                }

                // Rejections up to the allowance still read as ordinary auth failures.
                Assert.All(
                    responses.Take(LowInvalidKeyLimitFactory.AttemptLimit),
                    r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));

                var throttled = responses[LowInvalidKeyLimitFactory.AttemptLimit];
                Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
                Assert.NotNull(throttled.Headers.RetryAfter);
            }

            [Fact]
            public async Task ValidKeys_DoNotAccumulateAgainstTheAllowance()
            {
                var client = _factory.CreateClient();

                // Mint a real key, then use it well past the failed-attempt allowance. Only
                // rejections count, so a busy legitimate integration must never be throttled.
                var create = new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys");
                create.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
                create.Content = JsonContent.Create(new { Description = "Rate limit allowance test" });
                var created = await client.SendAsync(create);
                Assert.Equal(HttpStatusCode.OK, created.StatusCode);

                var publicKey = (await created.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("publicKey").GetString();

                for (var i = 0; i < LowInvalidKeyLimitFactory.AttemptLimit + 3; i++)
                {
                    var response = await client.SendAsync(WithKey(publicKey!));
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }
            }
        }

        public class StripeWebhookExemption : IClassFixture<LowAnonymousLimitFactory>
        {
            private readonly LowAnonymousLimitFactory _factory;

            public StripeWebhookExemption(LowAnonymousLimitFactory factory) => _factory = factory;

            [Fact]
            public async Task StripeWebhook_IsExemptFromTheAnonymousBudget()
            {
                var client = _factory.CreateClient();

                // Well past the budget. The webhook carries no valid Stripe-Signature so it fails
                // its own validation, but it must never fail with 429 — a dropped billing event is
                // the failure this exemption exists to prevent.
                for (var i = 0; i < Limit + 3; i++)
                {
                    var response = await client.PostAsync(
                        "/api/Subscription/webhook",
                        new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

                    // Assert the exact status the unsigned payload should produce, not just
                    // "not 429": the endpoint 404ing or 500ing would pass a NotEqual check while
                    // proving nothing about the exemption.
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                }
            }
        }
    }
}
