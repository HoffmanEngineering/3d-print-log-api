using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Webhook;
using PrintLogApi.Services;
using PrintLogApi.Webhooks;
using Xunit;

namespace PrintLogApi.IntegrationTests.Webhooks;

/// <summary>
/// Managing webhook subscriptions (#235): interactive users only, the secret shown once and stored
/// encrypted, the URL checked against the SSRF policy, and a per-tier count. Each test arranges its
/// own user, since limits are per user and the database is shared.
/// </summary>
public class WebhooksControllerTests(WebhookTestFactory factory) : IClassFixture<WebhookTestFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string HookUrl(string host = "hooks.example.test") => $"https://{host}/api/webhook/{Guid.NewGuid():N}";

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, User user, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await _client.SendAsync(request, Ct);
    }

    private async Task<WebhookSubscriptionCreatedDto> CreateAsync(User user, string? url = null, string[]? events = null, string? description = null)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/Webhooks", user, new
        {
            url = url ?? HookUrl(),
            events = events ?? ["print.finished", "print.failed"],
            description,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<WebhookSubscriptionCreatedDto>(Ct))!;
    }

    private async Task<T> WithDbAsync<T>(Func<PrintLogContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<PrintLogContext>());
    }

    private static async Task<string?> ProblemCode(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return json.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    #region Creating

    [Fact]
    public async Task Create_ReturnsTheSubscription_AndItsSecretOnce()
    {
        var user = await factory.CreateUserAsync();
        var url = HookUrl();

        var created = await CreateAsync(user, url, ["print.started", "print.finished"], "Home Assistant");

        Assert.Equal(url, created.Url);
        Assert.Equal("Home Assistant", created.Description);
        Assert.Equal(["print.started", "print.finished"], created.Events);
        Assert.True(created.Enabled);
        Assert.Equal(0, created.ConsecutiveFailures);
        Assert.Matches("^whsec_[A-Za-z0-9_-]{43}$", created.Secret);
    }

    [Fact]
    public async Task Create_StoresTheSecretEncrypted_AndRecoverably()
    {
        var user = await factory.CreateUserAsync();
        var created = await CreateAsync(user);

        var stored = await WithDbAsync(db => db.WebhookSubscriptions.SingleAsync(s => s.Id == created.Id, Ct));
        var protector = factory.Services.GetRequiredService<WebhookSecretProtector>();

        Assert.DoesNotContain(created.Secret!, stored.EncryptedSecret);
        Assert.Equal(created.Secret, protector.Unprotect(stored.Id, stored.EncryptedSecret));
    }

    [Fact]
    public async Task ListAndGet_NeverIncludeTheSecret()
    {
        var user = await factory.CreateUserAsync();
        var created = await CreateAsync(user);

        var list = await (await SendAsync(HttpMethod.Get, "/api/Webhooks", user)).Content.ReadAsStringAsync(Ct);
        var one = await (await SendAsync(HttpMethod.Get, $"/api/Webhooks/{created.Id}", user)).Content.ReadAsStringAsync(Ct);

        Assert.Contains(created.Id.ToString(), list);
        Assert.DoesNotContain(created.Secret!, list);
        Assert.DoesNotContain("secret", list, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(created.Secret!, one);
        Assert.DoesNotContain("secret", one, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_DefaultsToEveryEvent_WhenNoneAreGiven()
    {
        var user = await factory.CreateUserAsync();
        var response = await SendAsync(HttpMethod.Post, "/api/Webhooks", user, new { url = HookUrl() });
        var created = (await response.Content.ReadFromJsonAsync<WebhookSubscriptionCreatedDto>(Ct))!;

        Assert.Equal(["print.started", "print.finished", "print.failed"], created.Events);
    }

    [Theory]
    [InlineData("print.exploded")]
    [InlineData("")]
    public async Task Create_RejectsAnUnknownEvent(string eventName)
    {
        var user = await factory.CreateUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/Webhooks", user, new { url = HookUrl(), events = new[] { eventName } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_RejectsAnInvalidUrl()
    {
        var user = await factory.CreateUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/Webhooks", user, new { url = "ftp://hooks.example.test/x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_webhook_url", await ProblemCode(response));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8123/api/webhook/abc")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://192.168.1.20:8123/api/webhook/abc")]
    public async Task Create_RejectsAnInternalAddress(string url)
    {
        var user = await factory.CreateUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/Webhooks", user, new { url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("blocked_webhook_address", await ProblemCode(response));
        Assert.Equal(0, await WithDbAsync(db => db.WebhookSubscriptions.CountAsync(s => s.UserId == user.Id, Ct)));
    }

    [Fact]
    public async Task Create_RejectsAHostnameThatResolvesInternally()
    {
        var user = await factory.CreateUserAsync();
        factory.Resolver.Map("internal.example.test", "10.1.2.3");

        var response = await SendAsync(HttpMethod.Post, "/api/Webhooks", user, new { url = HookUrl("internal.example.test") });

        Assert.Equal("blocked_webhook_address", await ProblemCode(response));
    }

    [Fact]
    public async Task Free_AllowsOneSubscription()
    {
        var user = await factory.CreateUserAsync();
        await CreateAsync(user);

        var response = await SendAsync(HttpMethod.Post, "/api/Webhooks", user, new { url = HookUrl() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("webhook_limit", json.GetProperty("code").GetString());
        Assert.Equal(SubscriptionLimits.FreeMaxWebhooks, json.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Pro_AllowsTen()
    {
        var user = await factory.CreateUserAsync(pro: true);
        for (var i = 0; i < SubscriptionLimits.ProMaxWebhooks; i++)
        {
            await CreateAsync(user);
        }

        var response = await SendAsync(HttpMethod.Post, "/api/Webhooks", user, new { url = HookUrl() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(10, SubscriptionLimits.ProMaxWebhooks);
    }

    [Fact]
    public async Task Deleting_FreesTheSlot()
    {
        var user = await factory.CreateUserAsync();
        var first = await CreateAsync(user);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/Webhooks/{first.Id}", user)).StatusCode);

        await CreateAsync(user);
    }

    #endregion

    #region Ownership and authentication

    [Fact]
    public async Task AnotherUsersSubscription_IsNotFound_Everywhere()
    {
        var owner = await factory.CreateUserAsync();
        var other = await factory.CreateUserAsync();
        var created = await CreateAsync(owner);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/Webhooks/{created.Id}", other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"/api/Webhooks/{created.Id}", other, new { url = HookUrl(), enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/api/Webhooks/{created.Id}/rotate-secret", other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/api/Webhooks/{created.Id}/test", other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/api/Webhooks/{created.Id}", other)).StatusCode);
        var list = await (await SendAsync(HttpMethod.Get, "/api/Webhooks", other)).Content.ReadFromJsonAsync<List<WebhookSubscriptionDto>>(Ct);
        Assert.Empty(list!);
        Assert.True(await WithDbAsync(db => db.WebhookSubscriptions.AnyAsync(s => s.Id == created.Id && s.Enabled, Ct)));
    }

    [Fact]
    public async Task AnApiKeyCaller_CannotManageWebhooks()
    {
        var user = await factory.CreateUserAsync();
        string apiKey;
        using (var scope = factory.Services.CreateScope())
        {
            apiKey = (await scope.ServiceProvider.GetRequiredService<IUserApiKeyService>()
                .GenerateNewApiKey(user.Id, "webhook-test")).PublicKey!;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Webhooks") { Content = JsonContent.Create(new { url = HookUrl() }) };
        request.Headers.Add("X-Api-Key", apiKey);
        var response = await _client.SendAsync(request, Ct);
        var list = new HttpRequestMessage(HttpMethod.Get, "/api/Webhooks");
        list.Headers.Add("X-Api-Key", apiKey);
        var listResponse = await _client.SendAsync(list, Ct);

        Assert.False(response.IsSuccessStatusCode);
        Assert.False(listResponse.IsSuccessStatusCode);
        Assert.Equal(0, await WithDbAsync(db => db.WebhookSubscriptions.CountAsync(s => s.UserId == user.Id, Ct)));
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized()
    {
        var response = await _client.GetAsync("/api/Webhooks", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region Updating

    [Fact]
    public async Task Update_ChangesUrlEventsDescriptionAndEnabled_ButNotTheSecret()
    {
        var user = await factory.CreateUserAsync();
        var created = await CreateAsync(user);
        var before = await WithDbAsync(db => db.WebhookSubscriptions.AsNoTracking().SingleAsync(s => s.Id == created.Id, Ct));
        var newUrl = HookUrl("other.example.test");

        var response = await SendAsync(HttpMethod.Put, $"/api/Webhooks/{created.Id}", user,
            new { url = newUrl, events = new[] { "print.failed" }, description = "Lights", enabled = false });
        var updated = (await response.Content.ReadFromJsonAsync<WebhookSubscriptionDto>(Ct))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(newUrl, updated.Url);
        Assert.Equal(["print.failed"], updated.Events);
        Assert.Equal("Lights", updated.Description);
        Assert.False(updated.Enabled);
        var after = await WithDbAsync(db => db.WebhookSubscriptions.AsNoTracking().SingleAsync(s => s.Id == created.Id, Ct));
        Assert.Equal(before.EncryptedSecret, after.EncryptedSecret);
    }

    [Fact]
    public async Task Update_ChecksTheNewUrl()
    {
        var user = await factory.CreateUserAsync();
        var created = await CreateAsync(user);

        var response = await SendAsync(HttpMethod.Put, $"/api/Webhooks/{created.Id}", user, new { url = "http://10.0.0.8/hook" });

        Assert.Equal("blocked_webhook_address", await ProblemCode(response));
    }

    [Fact]
    public async Task TurningBackOn_ClearsTheFailuresAndTheAutoDisable()
    {
        var user = await factory.CreateUserAsync();
        var created = await CreateAsync(user);
        await WithDbAsync(db => db.WebhookSubscriptions.Where(s => s.Id == created.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Enabled, false)
            .SetProperty(x => x.ConsecutiveFailures, 3)
            .SetProperty(x => x.AutoDisabledAt, DateTime.UtcNow), Ct));

        var response = await SendAsync(HttpMethod.Put, $"/api/Webhooks/{created.Id}", user, new { url = created.Url, enabled = true });
        var updated = (await response.Content.ReadFromJsonAsync<WebhookSubscriptionDto>(Ct))!;

        Assert.True(updated.Enabled);
        Assert.Equal(0, updated.ConsecutiveFailures);
        Assert.Null(updated.AutoDisabledAt);
    }

    [Fact]
    public async Task RotateSecret_ReturnsANewSecret_AndReplacesTheOld()
    {
        var user = await factory.CreateUserAsync();
        var created = await CreateAsync(user);

        var response = await SendAsync(HttpMethod.Post, $"/api/Webhooks/{created.Id}/rotate-secret", user);
        var rotated = (await response.Content.ReadFromJsonAsync<WebhookSubscriptionCreatedDto>(Ct))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(created.Id, rotated.Id);
        Assert.NotEqual(created.Secret, rotated.Secret);
        var stored = await WithDbAsync(db => db.WebhookSubscriptions.SingleAsync(s => s.Id == created.Id, Ct));
        Assert.Equal(rotated.Secret, factory.Services.GetRequiredService<WebhookSecretProtector>().Unprotect(stored.Id, stored.EncryptedSecret));
    }

    #endregion

    #region Test event

    [Fact]
    public async Task Test_SendsASignedTestEvent_AndReportsTheResult()
    {
        var user = await factory.CreateUserAsync();
        var host = $"test-{Guid.NewGuid():N}.example.test";
        var created = await CreateAsync(user, HookUrl(host));

        var response = await SendAsync(HttpMethod.Post, $"/api/Webhooks/{created.Id}/test", user);
        var result = (await response.Content.ReadFromJsonAsync<WebhookTestResultDto>(Ct))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.Delivered);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("success", result.Result);
        var received = Assert.Single(factory.Receiver.Received(host));
        Assert.Equal(HttpMethod.Post, received.Method);
        Assert.Equal("webhook.test", received.Headers["X-PrintLog-Event"]);
        var body = JsonDocument.Parse(received.Body).RootElement;
        Assert.Equal("webhook.test", body.GetProperty("event").GetString());
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        var timestamp = long.Parse(received.Headers["X-PrintLog-Signature"].Split(',')[0][2..]);
        Assert.Equal(
            WebhookSignature.Create(created.Secret!, timestamp, System.Text.Encoding.UTF8.GetBytes(received.Body)),
            received.Headers["X-PrintLog-Signature"]);
    }

    [Fact]
    public async Task Test_WithAnUndecryptableSecret_SaysSo_InsteadOfFailing()
    {
        var user = await factory.CreateUserAsync();
        var host = $"test-{Guid.NewGuid():N}.example.test";
        var created = await CreateAsync(user, HookUrl(host));
        var foreign = factory.Services.GetRequiredService<WebhookSecretProtector>().Protect(Guid.NewGuid(), "whsec_other");
        await WithDbAsync(db => db.WebhookSubscriptions.Where(s => s.Id == created.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.EncryptedSecret, foreign), Ct));

        var response = await SendAsync(HttpMethod.Post, $"/api/Webhooks/{created.Id}/test", user);
        var result = (await response.Content.ReadFromJsonAsync<WebhookTestResultDto>(Ct))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(result.Delivered);
        Assert.Equal("secret_unavailable", result.Result);
        Assert.Empty(factory.Receiver.Received(host));
    }

    [Fact]
    public async Task Test_ReportsAFailure_WithoutRetrying_OrCountingIt()
    {
        var user = await factory.CreateUserAsync();
        var host = $"test-{Guid.NewGuid():N}.example.test";
        var created = await CreateAsync(user, HookUrl(host));
        factory.Receiver.Respond(host, HttpStatusCode.InternalServerError);

        var result = (await (await SendAsync(HttpMethod.Post, $"/api/Webhooks/{created.Id}/test", user))
            .Content.ReadFromJsonAsync<WebhookTestResultDto>(Ct))!;

        Assert.False(result.Delivered);
        Assert.Equal(500, result.StatusCode);
        Assert.Equal("http_error", result.Result);
        Assert.Single(factory.Receiver.Received(host));
        var stored = await WithDbAsync(db => db.WebhookSubscriptions.AsNoTracking().SingleAsync(s => s.Id == created.Id, Ct));
        Assert.Equal(0, stored.ConsecutiveFailures);
    }

    #endregion
}

/// <summary>Without an encryption key, webhooks are unavailable and say so, and the app still runs.</summary>
public class WebhooksUnavailableTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task Create_Returns503_WhenNoKeyIsConfigured()
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Webhooks")
        {
            Content = JsonContent.Create(new { url = "https://hooks.example.test/api/webhook/x" }),
        };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("webhooks_unavailable", json.GetProperty("code").GetString());
    }
}
