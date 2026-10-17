using System.IO.Compression;
using System.Net;
using System.Text;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// The anonymous per-address budget on <c>POST /api/Slicer/uploads</c>: a per-minute window and a
/// per-day window, both of which must allow a request; and the per-user budget on
/// <c>GET /api/Slicer/uploads/pending</c>. The suite runs with both off; each class
/// here gets its own factory, and so its own limiter, because every test request comes from the
/// same address.
/// </summary>
public class SlicerUploadRateLimitTests
{
    private const int Limit = 2;

    public sealed class PerMinuteFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Slicer:UploadRateLimitPerMinute"] = Limit.ToString(),
                    ["Slicer:UploadRateLimitPerDay"] = "0",
                }));
            base.ConfigureWebHost(builder);
        }
    }

    public sealed class PerDayFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Slicer:UploadRateLimitPerMinute"] = "0",
                    ["Slicer:UploadRateLimitPerDay"] = Limit.ToString(),
                }));
            base.ConfigureWebHost(builder);
        }
    }

    public class PerMinute(PerMinuteFactory factory) : IClassFixture<PerMinuteFactory>
    {
        [Fact]
        public async Task ExceedingTheMinuteBudget_Returns429_WithRetryAfterInsideAMinute()
        {
            var last = await ExhaustAsync(factory.CreateClient());

            Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
            var retryAfter = last.Headers.RetryAfter?.Delta;
            Assert.NotNull(retryAfter);
            Assert.InRange(retryAfter.Value, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        }
    }

    public class PerDay(PerDayFactory factory) : IClassFixture<PerDayFactory>
    {
        [Fact]
        public async Task ExceedingTheDayBudget_Returns429_WithRetryAfterBeyondAMinute()
        {
            var last = await ExhaustAsync(factory.CreateClient());

            Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
            var retryAfter = last.Headers.RetryAfter?.Delta;
            Assert.NotNull(retryAfter);
            Assert.InRange(retryAfter.Value, TimeSpan.FromHours(23), TimeSpan.FromDays(1));
        }
    }

    public sealed class PendingFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Slicer:PendingRateLimitPerMinute"] = Limit.ToString(),
                }));
            base.ConfigureWebHost(builder);
        }
    }

    /// <summary><c>GET /api/Slicer/uploads/pending</c>: a per-user minute budget of its own.</summary>
    public class Pending(PendingFactory factory) : IClassFixture<PendingFactory>
    {
        [Fact]
        public async Task ExceedingTheMinuteBudget_Returns429_ForThatUserOnly()
        {
            var client = factory.CreateClient();
            var responses = new List<HttpResponseMessage>();
            for (var i = 0; i < Limit + 1; i++)
            {
                responses.Add(await client.SendAsync(PendingRequest(IntegrationTestSeeder.TestUserOAuthId), TestContext.Current.CancellationToken));
            }

            Assert.All(responses.Take(Limit), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            Assert.Equal(HttpStatusCode.TooManyRequests, responses[Limit].StatusCode);
            Assert.NotNull(responses[Limit].Headers.RetryAfter?.Delta);

            // Partitioned by user, not by address: everyone here shares one.
            var other = await client.SendAsync(PendingRequest(IntegrationTestSeeder.SecondaryUserOAuthId), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }

        private static HttpRequestMessage PendingRequest(string user)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Slicer/uploads/pending?installId=0123456789abcdef0123456789abcdef");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, user);
            return request;
        }
    }

    /// <summary>Sends one more upload than the budget allows; the ones inside it must succeed.</summary>
    private static async Task<HttpResponseMessage> ExhaustAsync(HttpClient client)
    {
        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < Limit + 1; i++)
        {
            responses.Add(await client.PostAsync("/api/Slicer/uploads", Upload(), TestContext.Current.CancellationToken));
        }

        // Success, not merely "not 429": a mistyped route would never reach the limiter.
        Assert.All(responses.Take(Limit), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        return responses[Limit];
    }

    private static MultipartFormDataContent Upload()
    {
        using var gzipped = new MemoryStream();
        using (var gzip = new GZipStream(gzipped, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes("; generated by OrcaSlicer 2.2.0 on 2026-10-17 at 10:00:00\n; layer_height = 0.2\n"));
        }

        return new MultipartFormDataContent
        {
            { new ByteArrayContent(gzipped.ToArray()), "gcode", "upload.gcode.gz" },
            { new StringContent("""{ "pluginVersion": "0.1.0", "template": "default" }"""), "metadata" },
        };
    }
}
