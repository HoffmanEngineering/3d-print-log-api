using System.Net;
using PrintLogApi.Webhooks;
using Xunit;

namespace PrintLogApi.IntegrationTests.Webhooks;

/// <summary>
/// The SSRF guard (#235): URL validation when a subscription is saved, and the connect-time check in
/// the delivery handler, exercised against a real loopback socket.
/// </summary>
public class WebhookAddressGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebhookAddressGuard Guard(FakeWebhookHostResolver resolver, bool allowPrivate = false) =>
        new(resolver, allowPrivate);

    private static FakeWebhookHostResolver Resolver() => new FakeWebhookHostResolver()
        .Map("hooks.example.test", "93.184.215.14")
        .Map("localhost", "127.0.0.1")
        .Map("ha.lan.test", "192.168.1.20")
        .Map("mixed.example.test", "93.184.215.14", "10.0.0.5")
        .Map("metadata.example.test", "169.254.169.254");

    #region Validation when saving

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/api/webhook/abc")]
    [InlineData("hooks.example.test/api/webhook/abc")]
    [InlineData("ftp://hooks.example.test/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://hooks.example.test/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@hooks.example.test/x")]
    public async Task Validate_RejectsWhatIsNotAPlainHttpUrl(string? url)
    {
        var result = await Guard(Resolver()).ValidateAsync(url, Ct);

        Assert.Equal(WebhookUrlStatus.Invalid, result.Status);
        Assert.Null(result.Uri);
    }

    [Fact]
    public async Task Validate_RejectsAnOverlongUrl()
    {
        var url = "https://hooks.example.test/" + new string('a', 2048);

        Assert.Equal(WebhookUrlStatus.Invalid, (await Guard(Resolver()).ValidateAsync(url, Ct)).Status);
    }

    [Fact]
    public async Task Validate_RejectsAHostThatDoesNotResolve()
    {
        var result = await Guard(Resolver()).ValidateAsync("https://nowhere.example.test/x", Ct);

        Assert.Equal(WebhookUrlStatus.Invalid, result.Status);
        Assert.Contains("resolve", result.Error);
    }

    [Theory]
    [InlineData("https://hooks.example.test/api/webhook/abc")]
    [InlineData("http://hooks.example.test:8123/api/webhook/abc")]
    [InlineData("https://93.184.215.14/x")]
    public async Task Validate_AcceptsAPublicUrl(string url)
    {
        var result = await Guard(Resolver()).ValidateAsync(url, Ct);

        Assert.Equal(WebhookUrlStatus.Ok, result.Status);
        Assert.Equal(new Uri(url), result.Uri);
    }

    [Theory]
    [InlineData("http://localhost:8123/api/webhook/abc")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://2130706433/x")] // 127.0.0.1 as one decimal number
    [InlineData("http://0x7f.1/x")]
    [InlineData("http://[::1]/x")]
    [InlineData("http://[::ffff:127.0.0.1]/x")]
    [InlineData("http://10.0.0.1/x")]
    [InlineData("http://ha.lan.test:8123/x")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://metadata.example.test/")]
    [InlineData("http://0.0.0.0/x")]
    [InlineData("https://mixed.example.test/x")] // one private answer is enough to refuse
    public async Task Validate_BlocksInternalAddresses(string url)
    {
        var result = await Guard(Resolver()).ValidateAsync(url, Ct);

        Assert.Equal(WebhookUrlStatus.Blocked, result.Status);
    }

    [Theory]
    [InlineData("http://localhost:8123/api/webhook/abc", WebhookUrlStatus.Ok)]
    [InlineData("http://ha.lan.test:8123/x", WebhookUrlStatus.Ok)]
    [InlineData("http://169.254.169.254/latest/meta-data/", WebhookUrlStatus.Blocked)]
    [InlineData("http://metadata.example.test/", WebhookUrlStatus.Blocked)]
    [InlineData("http://[::ffff:192.168.1.20]/x", WebhookUrlStatus.Blocked)]
    public async Task Validate_WithPrivateAllowed_StillBlocksForbiddenAddresses(string url, WebhookUrlStatus expected)
    {
        var result = await Guard(Resolver(), allowPrivate: true).ValidateAsync(url, Ct);

        Assert.Equal(expected, result.Status);
    }

    #endregion

    #region Connect time

    private static HttpClient Client(WebhookAddressGuard guard) =>
        new(WebhookHttpHandlerFactory.Create(guard, TimeSpan.FromSeconds(5))) { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task Connect_RefusesLoopback_WithoutOpeningASocketToIt()
    {
        await using var server = new LoopbackHttpServer(LoopbackHttpServer.Ok());
        using var client = Client(Guard(Resolver()));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{server.Port}/", Ct));

        Assert.IsType<WebhookAddressBlockedException>(ex.InnerException);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task Connect_RefusesARebindingAnswer_ThatValidatedAsPublic()
    {
        await using var server = new LoopbackHttpServer(LoopbackHttpServer.Ok());
        var resolver = Resolver();
        var guard = Guard(resolver);
        var url = $"http://rebind.example.test:{server.Port}/hook";

        // Public when the subscription is saved...
        resolver.Map("rebind.example.test", "93.184.215.14");
        Assert.Equal(WebhookUrlStatus.Ok, (await guard.ValidateAsync(url, Ct)).Status);

        // ...and loopback by the time it is delivered.
        resolver.Map("rebind.example.test", "127.0.0.1");
        using var client = Client(guard);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(url, new StringContent("{}"), Ct));

        Assert.IsType<WebhookAddressBlockedException>(ex.InnerException);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task Connect_WithPrivateAllowed_ReachesALocalServer()
    {
        await using var server = new LoopbackHttpServer(LoopbackHttpServer.Ok());
        var resolver = Resolver().Map("homeassistant.local.test", "127.0.0.1");
        using var client = Client(Guard(resolver, allowPrivate: true));

        using var response = await client.PostAsync($"http://homeassistant.local.test:{server.Port}/api/webhook/abc", new StringContent("{}"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task Connect_SkipsAForbiddenAnswer_AndUsesAnAllowedOne()
    {
        await using var server = new LoopbackHttpServer(LoopbackHttpServer.Ok());
        var resolver = Resolver().Map("two.local.test", "169.254.169.254", "127.0.0.1");
        using var client = Client(Guard(resolver, allowPrivate: true));

        using var response = await client.GetAsync($"http://two.local.test:{server.Port}/", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Connect_DoesNotFollowRedirects()
    {
        await using var target = new LoopbackHttpServer(LoopbackHttpServer.Ok());
        await using var server = new LoopbackHttpServer(
            $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{target.Port}/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var client = Client(Guard(Resolver(), allowPrivate: true));

        using var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(0, target.Connections);
    }

    #endregion
}
