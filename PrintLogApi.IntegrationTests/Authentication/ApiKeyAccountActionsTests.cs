using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.IntegrationTests.Email;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.User;
using PrintLogApi.Models.DTOs.UserApiKeys;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Authentication;

/// <summary>
/// A personal API key lives in slicer and printer config files and is accepted in the query
/// string, so a leaked one must not be able to take over the account: mint or revoke keys,
/// deactivate the account, edit the profile, or open billing sessions. These routes carry
/// <c>InteractiveUserOnly</c>, which pins the bearer scheme, so the key is challenged (401)
/// rather than authenticated and then forbidden, as <c>GET Users/me/email</c> already is.
/// </summary>
public class ApiKeyAccountActionsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ApiKeyAccountActionsTests(CustomWebApplicationFactory factory) => _factory = factory;

    public static TheoryData<string, string> AccountActions => new()
    {
        { "GET", "/api/UserApiKeys" },
        { "POST", "/api/UserApiKeys" },
        { "DELETE", "/api/UserApiKeys/{keyId}" },
        { "POST", "/api/Users/me/deactivate" },
        { "POST", "/api/Users/me/reactivate" },
        { "PUT", "/api/Users/me" },
        { "POST", "/api/Users/me/profile-image" },
        { "POST", "/api/Users/me/cover-image" },
        { "DELETE", "/api/Users/me/cover-image" },
        { "POST", "/api/Subscription/checkout" },
        { "POST", "/api/Subscription/portal" },
    };

    [Theory]
    [MemberData(nameof(AccountActions))]
    public async Task AccountAction_WithApiKey_IsRejected(string method, string path)
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, key) = await CreateUserWithKeyAsync();

        using var request = BuildRequest(method, path.Replace("{keyId}", key.Id.ToString(), StringComparison.Ordinal));
        request.Headers.Add("X-Api-Key", key.PublicKey);
        var response = await _factory.CreateClient().SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Nothing changed: no new key, the key is still active, the account is untouched.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var keys = await db.UserApiKeys.AsNoTracking().Where(k => k.UserId == user.Id).ToListAsync(ct);
        var only = Assert.Single(keys);
        Assert.False(only.IsDeleted);

        var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, ct);
        Assert.Null(stored.DeactivationDateTime);
        Assert.Null(stored.DisplayName);
        Assert.Null(stored.ProfilePicture);
        Assert.Equal("https://example.blob.core.windows.net/userprofile/cover.png", stored.CoverPicture);
        Assert.Equal(User.ProfileViewStatus.Public, stored.ViewStatus);
    }

    // The query-string form of the key is the same credential and gets the same answer.
    [Fact]
    public async Task MintKey_WithApiKeyInQuery_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, key) = await CreateUserWithKeyAsync();

        using var request = BuildRequest("POST", $"/api/UserApiKeys?api_key={key.PublicKey}");
        var response = await _factory.CreateClient().SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.Equal(1, await db.UserApiKeys.CountAsync(k => k.UserId == user.Id, ct));
    }

    // The read-only routes integrations use (printlog-bridge `doctor` and the Pro check) keep
    // working with a key.
    [Theory]
    [InlineData("/api/Users/me")]
    [InlineData("/api/Subscription/me")]
    public async Task IntegrationReads_WithApiKey_StillWork(string path)
    {
        var (_, key) = await CreateUserWithKeyAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Api-Key", key.PublicKey);
        var response = await _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpRequestMessage BuildRequest(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (path.Contains("image", StringComparison.Ordinal) && method == "POST")
        {
            var form = new MultipartFormDataContent();
            var image = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(image, "image", "picture.jpg");
            request.Content = form;
        }
        else if (path.StartsWith("/api/UserApiKeys", StringComparison.Ordinal) && method == "POST")
        {
            request.Content = JsonContent.Create(new AddNewApiKeyDto { Description = "minted with a key" });
        }
        else if (path == "/api/Users/me" && method == "PUT")
        {
            request.Content = JsonContent.Create(new UpdateUserDetailDto
            {
                DisplayName = "Taken over",
                ProfilePicture = "https://attacker.example/p.gif",
                ViewStatus = User.ProfileViewStatus.Private,
            });
        }
        else if (path == "/api/Subscription/checkout")
        {
            request.Content = JsonContent.Create(new { planId = "pro_monthly" });
        }

        return request;
    }

    private async Task<(User User, NewUserApiKeyDto Key)> CreateUserWithKeyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db);
        user.CoverPicture = "https://example.blob.core.windows.net/userprofile/cover.png";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var key = await scope.ServiceProvider.GetRequiredService<IUserApiKeyService>()
            .GenerateNewApiKey(user.Id, "account actions test");
        return (user, key);
    }
}
