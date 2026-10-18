using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailVerificationRefresherTests : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CustomWebApplicationFactory _factory;

    public EmailVerificationRefresherTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        // The stub is a singleton shared by every test in the class, so reset every knob.
        _factory.Auth0.EmailStatus = null;
        _factory.Auth0.ThrowOnEmailStatus = false;
        _factory.Auth0.EmailStatusCalls = 0;
    }

    // A fresh user per test: the throttle flag lives in the process-wide IMemoryCache.
    private async Task<(long Id, string OAuthId)> SeedAsync(bool verified)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db, verified: verified);
        return (user.Id, user.OAuthUserId!);
    }

    private async Task<bool> RefreshAsync(long userId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEmailVerificationRefresher>().RefreshAsync(userId, Ct);
    }

    [Fact]
    public async Task AlreadyVerified_NoAuth0Call()
    {
        var (id, _) = await SeedAsync(verified: true);

        Assert.True(await RefreshAsync(id));
        Assert.Equal(0, _factory.Auth0.EmailStatusCalls);
    }

    [Fact]
    public async Task Auth0Verified_UpdatesUserAndReturnsTrue()
    {
        var (id, _) = await SeedAsync(verified: false);
        _factory.Auth0.EmailStatus = new Auth0EmailStatus("  New.Address@Example.com ", true);

        Assert.True(await RefreshAsync(id));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, Ct);
        Assert.True(user.EmailVerified);
        Assert.Equal("New.Address@Example.com", user.Email);
        Assert.NotNull(user.EmailUpdatedAt);
    }

    [Fact]
    public async Task Auth0Unverified_ReturnsFalse()
    {
        var (id, _) = await SeedAsync(verified: false);
        _factory.Auth0.EmailStatus = new Auth0EmailStatus("a@example.com", false);

        Assert.False(await RefreshAsync(id));
        Assert.Equal(1, _factory.Auth0.EmailStatusCalls);
    }

    [Fact]
    public async Task SecondCallWithinHour_IsThrottled()
    {
        var (id, _) = await SeedAsync(verified: false);
        _factory.Auth0.EmailStatus = new Auth0EmailStatus("a@example.com", false);

        Assert.False(await RefreshAsync(id));
        Assert.False(await RefreshAsync(id));

        Assert.Equal(1, _factory.Auth0.EmailStatusCalls);
    }

    [Fact]
    public async Task Auth0Throws_ReturnsFalseAndDoesNotThrow()
    {
        var (id, _) = await SeedAsync(verified: false);
        _factory.Auth0.ThrowOnEmailStatus = true;

        Assert.False(await RefreshAsync(id));
    }

    [Fact]
    public async Task OnboardingEndpoint_RefreshesUnverifiedUser()
    {
        var (_, oauthId) = await SeedAsync(verified: false);
        _factory.Auth0.EmailStatus = new Auth0EmailStatus("a@example.com", true);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/Users/me/onboarding");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthId);
        var response = await _factory.CreateClient().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = (await response.Content.ReadFromJsonAsync<OnboardingStatusDto>(Ct))!;
        Assert.True(status.EmailVerified);
    }
}
