using System.Globalization;
using System.Net;
using System.Text.Json;
using PrintLogApi.IntegrationTests.Email;
using PrintLogApi.Models.DTOs.UserApiKeys;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class VerificationEmailEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CustomWebApplicationFactory _factory;

    public VerificationEmailEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        // The fake is a shared singleton, so reset every knob this suite touches.
        _factory.Auth0.VerificationEmailsSent.Clear();
        _factory.Auth0.ThrowOnSendVerification = false;
        _factory.Auth0.ThrowOnEmailStatus = false;
        _factory.Auth0.EmailStatus = null;
    }

    // A fresh user per test: the throttle flag lives in the process-wide IMemoryCache.
    private async Task<(long Id, string OAuthId)> SeedUserAsync(bool verified)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db, verified: verified, createdDate: DateTimeOffset.UtcNow);
        return (user.Id, user.OAuthUserId!);
    }

    private Task<HttpResponseMessage> PostAsync(string oauthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/verification-email");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthId);
        return _factory.CreateClient().SendAsync(request, Ct);
    }

    [Fact]
    public async Task Unverified_Sends204_AndCallsAuth0()
    {
        var (id, oauthId) = await SeedUserAsync(verified: false);

        var response = await PostAsync(oauthId);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(oauthId, Assert.Single(_factory.Auth0.VerificationEmailsSent));
        var sent = Assert.Single(_factory.Telemetry.Events("VerificationEmail_Sent"),
            e => e.Properties["UserId"] == id.ToString(CultureInfo.InvariantCulture));
        Assert.NotNull(sent);
    }

    [Fact]
    public async Task SecondCallWithin5Min_Returns429WithRetryAfter()
    {
        var (_, oauthId) = await SeedUserAsync(verified: false);

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(oauthId)).StatusCode);
        var second = await PostAsync(oauthId);

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        var seconds = int.Parse(second.Headers.GetValues("Retry-After").Single(), CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 1, 300);
        var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("rate_limited", body.GetProperty("code").GetString());
        Assert.Single(_factory.Auth0.VerificationEmailsSent);
    }

    [Fact]
    public async Task Verified_Returns409AlreadyVerified()
    {
        var (_, oauthId) = await SeedUserAsync(verified: true);

        var response = await PostAsync(oauthId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("already_verified", body.GetProperty("code").GetString());
        Assert.DoesNotContain(oauthId, _factory.Auth0.VerificationEmailsSent);
    }

    [Fact]
    public async Task Auth0Fails_Returns502_AndDoesNotThrottle()
    {
        var (_, oauthId) = await SeedUserAsync(verified: false);
        _factory.Auth0.ThrowOnSendVerification = true;

        var first = await PostAsync(oauthId);
        var second = await PostAsync(oauthId);

        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, second.StatusCode);
        var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("auth0_unavailable", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ApiKey_IsRejected()
    {
        var client = _factory.CreateClient();

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys");
        create.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        create.Content = JsonContent.Create(new AddNewApiKeyDto { Description = "verification endpoint" });
        var key = (await (await client.SendAsync(create, Ct)).Content.ReadFromJsonAsync<NewUserApiKeyDto>(Ct))!;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/verification-email");
        request.Headers.Add("X-Api-Key", key.PublicKey);
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
