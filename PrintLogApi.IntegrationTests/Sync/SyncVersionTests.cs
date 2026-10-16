using System.Net;
using PrintLogApi.Models.DTOs.UserApiKeys;
using Xunit;

namespace PrintLogApi.IntegrationTests.Sync;

/// <summary>
/// <c>GET /api/sync/version</c> (#234): the change check an integration polls every minute.
/// </summary>
public class SyncVersionTests(SyncTestFactory factory) : IClassFixture<SyncTestFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record VersionBody(string Version);

    [Fact]
    public async Task Version_ReturnsAnOpaqueVersionWithAMatchingStrongETag()
    {
        var user = await factory.CreateUserAsync();

        var response = await _client.SendAsync(factory.Get(user, "/api/sync/version"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VersionBody>(Ct);
        Assert.NotNull(body);
        Assert.Matches("^[0-9a-f]{32}$", body.Version);
        var etag = response.Headers.ETag;
        Assert.NotNull(etag);
        Assert.False(etag.IsWeak);
        Assert.Equal($"\"{body.Version}\"", etag.Tag);
        Assert.Contains("no-cache", response.Headers.CacheControl?.ToString());
        Assert.True(response.Headers.CacheControl?.Private);
    }

    [Fact]
    public async Task Version_DoesNotExposeTheCacheGuid()
    {
        var user = await factory.CreateUserAsync();
        var guid = factory.Services.GetRequiredService<PrintLogApi.Services.ICacheVersionService>().GetUserCacheVersion(user.Id);

        var body = await (await _client.SendAsync(factory.Get(user, "/api/sync/version"), Ct))
            .Content.ReadFromJsonAsync<VersionBody>(Ct);

        Assert.DoesNotContain(guid[..32], body!.Version);
    }

    [Fact]
    public async Task Version_IsStableUntilAWrite_ThenChanges()
    {
        var user = await factory.CreateUserAsync();

        var first = await (await _client.SendAsync(factory.Get(user, "/api/sync/version"), Ct)).Content.ReadFromJsonAsync<VersionBody>(Ct);
        var again = await (await _client.SendAsync(factory.Get(user, "/api/sync/version"), Ct)).Content.ReadFromJsonAsync<VersionBody>(Ct);
        factory.Invalidate(user);
        var after = await (await _client.SendAsync(factory.Get(user, "/api/sync/version"), Ct)).Content.ReadFromJsonAsync<VersionBody>(Ct);

        Assert.Equal(first!.Version, again!.Version);
        Assert.NotEqual(first.Version, after!.Version);
    }

    [Fact]
    public async Task Version_DiffersBetweenUsers()
    {
        var a = await factory.CreateUserAsync();
        var b = await factory.CreateUserAsync();

        var va = await (await _client.SendAsync(factory.Get(a, "/api/sync/version"), Ct)).Content.ReadFromJsonAsync<VersionBody>(Ct);
        var vb = await (await _client.SendAsync(factory.Get(b, "/api/sync/version"), Ct)).Content.ReadFromJsonAsync<VersionBody>(Ct);

        Assert.NotEqual(va!.Version, vb!.Version);
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("W/{0}")]
    [InlineData("\"other\", {0}")]
    [InlineData("*")]
    public async Task Version_WithMatchingIfNoneMatch_Returns304WithoutABody(string format)
    {
        var user = await factory.CreateUserAsync();
        var first = await _client.SendAsync(factory.Get(user, "/api/sync/version"), Ct);
        var etag = first.Headers.ETag!.Tag;

        var response = await _client.SendAsync(factory.Get(user, "/api/sync/version", string.Format(format, etag)), Ct);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal(etag, response.Headers.ETag?.Tag);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Version_WithAStaleIfNoneMatch_Returns200()
    {
        var user = await factory.CreateUserAsync();
        var first = await _client.SendAsync(factory.Get(user, "/api/sync/version"), Ct);
        factory.Invalidate(user);

        var response = await _client.SendAsync(factory.Get(user, "/api/sync/version", first.Headers.ETag!.Tag), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(first.Headers.ETag.Tag, response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Version_Unauthenticated_Returns401()
    {
        var response = await _client.GetAsync("/api/sync/version", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Version_AcceptsAnApiKey()
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys");
        create.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        create.Content = JsonContent.Create(new AddNewApiKeyDto { Description = "sync version" });
        var key = await (await _client.SendAsync(create, Ct)).Content.ReadFromJsonAsync<NewUserApiKeyDto>(Ct);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/sync/version");
        request.Headers.Add("X-Api-Key", key!.PublicKey);
        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
