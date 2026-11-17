using System.Net;
using PrintLogApi.Email;
using PrintLogApi.IntegrationTests.Email;
using PrintLogApi.Models.DTOs.UserApiKeys;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class AccountEmailEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AccountEmailEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetAccountEmail_ReturnsEmailAndVerified()
    {
        var ct = TestContext.Current.CancellationToken;
        string oauthId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            oauthId = (await EmailTestData.CreateUserAsync(db, email: "me@example.com", verified: false)).OAuthUserId!;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/Users/me/email");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthId);
        var response = await _factory.CreateClient().SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new AccountEmailDto("me@example.com", false), await response.Content.ReadFromJsonAsync<AccountEmailDto>(ct));
    }

    // An API key lives in a printer config file; it must not be a way to read the account address.
    // InteractiveUserOnly pins the authentication scheme, so the key is challenged (401) rather
    // than authenticated and then forbidden.
    [Fact]
    public async Task GetAccountEmail_ApiKey_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = _factory.CreateClient();

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys");
        create.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        create.Content = JsonContent.Create(new AddNewApiKeyDto { Description = "email endpoint" });
        var key = (await (await client.SendAsync(create, ct)).Content.ReadFromJsonAsync<NewUserApiKeyDto>(ct))!;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/Users/me/email");
        request.Headers.Add("X-Api-Key", key.PublicKey);
        var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAccountEmail_Anonymous_IsUnauthorized()
    {
        var response = await _factory.CreateClient().GetAsync("/api/Users/me/email", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
