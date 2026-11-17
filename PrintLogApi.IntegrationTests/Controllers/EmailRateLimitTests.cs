using System.Net;
using PrintLogApi.Email;
using PrintLogApi.Email.Tokens;
using PrintLogApi.IntegrationTests.Email;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>The email token endpoints' own budgets, re-enabled here (the suite runs with them at 0).</summary>
public class EmailRateLimitTests : IClassFixture<EmailRateLimitTests.LimitedFactory>
{
    public const int PerToken = 10;

    public sealed class LimitedFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Email:RateLimits:PerTokenPerMinute"] = PerToken.ToString(),
                    ["Email:RateLimits:GlobalPerMinute"] = "1000",
                }));
            base.ConfigureWebHost(builder);
        }
    }

    private readonly LimitedFactory _factory;

    public EmailRateLimitTests(LimitedFactory factory) => _factory = factory;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<long> UserIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return (await EmailTestData.CreateUserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>())).Id;
    }

    private IEmailTokenService Tokens => _factory.Services.GetRequiredService<IEmailTokenService>();

    [Fact]
    public async Task RateLimit_PerToken_Query()
    {
        var token = Tokens.CreateUnsubscribe(await UserIdAsync(), EmailSettingTypes.MonthlyRecap);
        var client = _factory.CreateClient();

        async Task<HttpStatusCode> SendAsync() => (await client.PostAsync(
            $"/api/email/unsubscribe?t={token}",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("List-Unsubscribe", "One-Click")]),
            Ct)).StatusCode;

        for (var i = 0; i < PerToken; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await SendAsync());
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync());
    }

    [Fact]
    public async Task RateLimit_PerToken_Header()
    {
        var userId = await UserIdAsync();
        var token = Tokens.CreateManage(userId);
        var client = _factory.CreateClient();

        async Task<HttpStatusCode> GetAsync(string t)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/email/preferences");
            request.Headers.Add(EmailTokenHeader.Name, t);
            return (await client.SendAsync(request, Ct)).StatusCode;
        }

        for (var i = 0; i < PerToken; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await GetAsync(token));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await GetAsync(token));

        // Another user's token has its own budget.
        Assert.Equal(HttpStatusCode.OK, await GetAsync(Tokens.CreateManage(await UserIdAsync())));
    }
}
