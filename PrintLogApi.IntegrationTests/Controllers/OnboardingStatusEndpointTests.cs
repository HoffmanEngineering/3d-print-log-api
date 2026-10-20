using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PrintLogApi.IntegrationTests.Email;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Models.DTOs.UserApiKeys;
using PrintLogApi.Services;
using PrintLogApi.Users;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class OnboardingStatusEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly DateTimeOffset Launch = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CustomWebApplicationFactory _factory;

    public OnboardingStatusEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    private WebApplicationFactory<Startup> WithLaunchDate(DateTimeOffset? launch) =>
        _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Configure<UserOnboardingOptions>(o => o.LaunchDate = launch)));

    private async Task<(long Id, string OAuthId)> SeedUserAsync(DateTimeOffset? createdDate, bool verified = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db, verified: verified, createdDate: createdDate);
        return (user.Id, user.OAuthUserId!);
    }

    private static async Task<OnboardingStatusDto> GetStatusAsync(WebApplicationFactory<Startup> factory, string oauthId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/Users/me/onboarding");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthId);
        var response = await factory.CreateClient().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OnboardingStatusDto>(Ct))!;
    }

    [Fact]
    public async Task LaunchDateNull_IsNotEligible()
    {
        var (_, oauthId) = await SeedUserAsync(Launch.AddDays(1));
        using var factory = WithLaunchDate(null);

        var status = await GetStatusAsync(factory, oauthId);

        Assert.False(status.OnboardingEligible);
    }

    [Fact]
    public async Task CreatedAfterLaunch_IsEligible()
    {
        var created = Launch.AddDays(2);
        var (_, oauthId) = await SeedUserAsync(created, verified: false);
        using var factory = WithLaunchDate(Launch);

        var status = await GetStatusAsync(factory, oauthId);

        Assert.True(status.OnboardingEligible);
        Assert.Equal(created, status.CreatedDate);
        Assert.False(status.EmailVerified);
    }

    [Fact]
    public async Task CreatedBeforeLaunch_IsNotEligible()
    {
        var (_, oauthId) = await SeedUserAsync(Launch.AddDays(-1));
        using var factory = WithLaunchDate(Launch);

        var status = await GetStatusAsync(factory, oauthId);

        Assert.False(status.OnboardingEligible);
    }

    [Fact]
    public async Task NullCreatedDate_IsNotEligible()
    {
        var (_, oauthId) = await SeedUserAsync(null);
        using var factory = WithLaunchDate(Launch);

        var status = await GetStatusAsync(factory, oauthId);

        Assert.False(status.OnboardingEligible);
        Assert.Null(status.CreatedDate);
    }

    // The token claim lags until the next sign-in, so an eligible, unverified user is checked
    // against Auth0 (throttled).
    [Fact]
    public async Task EligibleUnverified_RefreshesFromAuth0()
    {
        var (_, oauthId) = await SeedUserAsync(Launch.AddDays(2), verified: false);
        using var factory = WithLaunchDate(Launch);
        var auth0 = (CustomWebApplicationFactory.TestAuth0Service)factory.Services.GetRequiredService<IAuth0Service>();
        auth0.EmailStatus = new Auth0EmailStatus("a@example.com", true);

        var status = await GetStatusAsync(factory, oauthId);

        Assert.True(status.EmailVerified);
        Assert.Equal(1, auth0.EmailStatusCalls);
    }

    // Existing users never see the onboarding surfaces, so their status must not cost an Auth0 call.
    [Fact]
    public async Task IneligibleUnverified_DoesNotCallAuth0()
    {
        var (_, oauthId) = await SeedUserAsync(Launch.AddDays(-1), verified: false);
        using var factory = WithLaunchDate(Launch);
        var auth0 = (CustomWebApplicationFactory.TestAuth0Service)factory.Services.GetRequiredService<IAuth0Service>();
        auth0.EmailStatus = new Auth0EmailStatus("a@example.com", true);

        var status = await GetStatusAsync(factory, oauthId);

        Assert.False(status.OnboardingEligible);
        Assert.Equal(0, auth0.EmailStatusCalls);
    }

    // The API key lives in a printer config file; onboarding state is for interactive sessions.
    [Fact]
    public async Task ApiKey_IsRejected()
    {
        var client = _factory.CreateClient();

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys");
        create.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        create.Content = JsonContent.Create(new AddNewApiKeyDto { Description = "onboarding endpoint" });
        var key = (await (await client.SendAsync(create, Ct)).Content.ReadFromJsonAsync<NewUserApiKeyDto>(Ct))!;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/Users/me/onboarding");
        request.Headers.Add("X-Api-Key", key.PublicKey);
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // GET /api/Users/{id} is anonymous, so the onboarding facts must stay off it.
    [Fact]
    public async Task PublicProfile_DoesNotExposeOnboardingFields()
    {
        var (id, _) = await SeedUserAsync(Launch.AddDays(2), verified: false);
        using var factory = WithLaunchDate(Launch);

        var response = await factory.CreateClient().GetAsync($"/api/Users/{id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var names = body.EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => string.Equals(n, "emailVerified", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => string.Equals(n, "createdDate", StringComparison.OrdinalIgnoreCase));
    }
}
