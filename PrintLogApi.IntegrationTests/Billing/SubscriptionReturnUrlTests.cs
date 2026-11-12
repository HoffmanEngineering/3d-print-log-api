using System.Net;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Billing;

/// <summary>
/// Stripe's success, cancel and return URLs come from the configured web base (<c>Email:WebBaseUrl</c>),
/// never from whatever <c>Origin</c> or <c>Host</c> the caller sends. Otherwise a genuine,
/// 3D Print Log-branded Stripe checkout could be made to send whoever pays to an attacker's page.
/// </summary>
[Collection(StripeClientCollection.Name)]
public class SubscriptionReturnUrlTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    /// <summary><c>Email:WebBaseUrl</c> in appsettings.IntegrationTesting.json.</summary>
    private const string WebBase = "https://www.3dprintlog.test";

    private const string CheckoutPath = "/v1/checkout/sessions";
    private const string PortalPath = "/v1/billing_portal/sessions";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Created before any test installs the fake, so building the host cannot replace it.
    private readonly HttpClient _client = factory.CreateClient();

    private static HttpRequestMessage Checkout(User user, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Subscription/checkout") { Content = JsonContent.Create(new { planId = "pro_monthly" }) };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    private static HttpRequestMessage Portal(User user, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Subscription/portal");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    private static async Task AssertOk(HttpResponseMessage response, string expectedStripeUrl)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        Assert.Contains(expectedStripeUrl, body);
    }

    [Fact]
    public async Task Checkout_FromTheConfiguredOrigin_SendsReturnUrlsUnderTheConfiguredBase()
    {
        var fake = new FakeStripe();
        using var _ = fake.Install();
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.None);

        var response = await _client.SendAsync(Checkout(user, WebBase), Ct);

        await AssertOk(response, "https://checkout.stripe.com/c/pay/cs_test_fake");
        var form = fake.SingleForm(CheckoutPath);
        Assert.Equal($"{WebBase}/subscription/success?session_id={{CHECKOUT_SESSION_ID}}", form["success_url"]);
        Assert.Equal($"{WebBase}/subscription/canceled", form["cancel_url"]);
        Assert.Equal(StripeTestData.CustomerId(user), form["customer"]);
    }

    [Fact]
    public async Task Portal_FromTheConfiguredOrigin_SendsAReturnUrlUnderTheConfiguredBase()
    {
        var fake = new FakeStripe();
        using var _ = fake.Install();
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.Active);

        var response = await _client.SendAsync(Portal(user, WebBase), Ct);

        await AssertOk(response, "https://billing.stripe.com/p/session/fake");
        Assert.Equal($"{WebBase}/settings", fake.SingleForm(PortalPath)["return_url"]);
    }

    [Fact]
    public async Task Checkout_FromAForeignOrigin_StillReturnsToTheConfiguredBase()
    {
        var fake = new FakeStripe();
        using var _ = fake.Install();
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.None);

        var response = await _client.SendAsync(Checkout(user, "https://evil.example"), Ct);

        await AssertOk(response, "https://checkout.stripe.com/c/pay/cs_test_fake");
        var form = fake.SingleForm(CheckoutPath);
        Assert.Equal($"{WebBase}/subscription/success?session_id={{CHECKOUT_SESSION_ID}}", form["success_url"]);
        Assert.Equal($"{WebBase}/subscription/canceled", form["cancel_url"]);
    }

    [Fact]
    public async Task Portal_FromAForeignOrigin_StillReturnsToTheConfiguredBase()
    {
        var fake = new FakeStripe();
        using var _ = fake.Install();
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.Active);

        var response = await _client.SendAsync(Portal(user, "https://evil.example"), Ct);

        await AssertOk(response, "https://billing.stripe.com/p/session/fake");
        Assert.Equal($"{WebBase}/settings", fake.SingleForm(PortalPath)["return_url"]);
    }

    [Theory]
    // Lookalikes of the configured origin: another port, a suffix, a scheme downgrade, a path.
    [InlineData("https://www.3dprintlog.test:8443")]
    [InlineData("https://www.3dprintlog.test.evil.example")]
    [InlineData("http://www.3dprintlog.test")]
    [InlineData("https://www.3dprintlog.test/evil")]
    [InlineData("null")]
    public async Task Checkout_FromALookalikeOrigin_ReturnsToTheConfiguredBase(string origin)
    {
        var fake = new FakeStripe();
        using var _ = fake.Install();
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.None);

        var response = await _client.SendAsync(Checkout(user, origin), Ct);

        await AssertOk(response, "https://checkout.stripe.com/c/pay/cs_test_fake");
        Assert.Equal($"{WebBase}/subscription/canceled", fake.SingleForm(CheckoutPath)["cancel_url"]);
    }

    [Fact]
    public async Task Checkout_WithoutAnOrigin_IgnoresTheHostHeader()
    {
        var fake = new FakeStripe();
        using var _ = fake.Install();
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.None);
        var request = Checkout(user);
        request.Headers.Host = "evil-host.example";

        var response = await _client.SendAsync(request, Ct);

        await AssertOk(response, "https://checkout.stripe.com/c/pay/cs_test_fake");
        var form = fake.SingleForm(CheckoutPath);
        Assert.Equal($"{WebBase}/subscription/success?session_id={{CHECKOUT_SESSION_ID}}", form["success_url"]);
        Assert.Equal($"{WebBase}/subscription/canceled", form["cancel_url"]);
    }

    [Fact]
    public async Task Portal_WithoutAnOrigin_IgnoresTheHostHeader()
    {
        var fake = new FakeStripe();
        using var _ = fake.Install();
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.Active);
        var request = Portal(user);
        request.Headers.Host = "evil-host.example";

        var response = await _client.SendAsync(request, Ct);

        await AssertOk(response, "https://billing.stripe.com/p/session/fake");
        Assert.Equal($"{WebBase}/settings", fake.SingleForm(PortalPath)["return_url"]);
    }

    /// <summary>A UI worktree on its own port, allowed by <c>Stripe:AllowedReturnOrigins</c> in Development.</summary>
    [Fact]
    public async Task CheckoutAndPortal_FromAnAllowListedOrigin_ReturnToThatOrigin()
    {
        const string worktree = "https://localhost:4310";
        var host = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stripe:AllowedReturnOrigins:0"] = "https://localhost:4311",
            ["Stripe:AllowedReturnOrigins:1"] = worktree + "/",
        })));
        // Build the host before installing the fake, so nothing Startup does can replace it.
        var client = host.CreateClient();
        var fake = new FakeStripe();
        using var _ = fake.Install();
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.Active);

        await AssertOk(await client.SendAsync(Checkout(user, worktree), Ct), "https://checkout.stripe.com/c/pay/cs_test_fake");
        await AssertOk(await client.SendAsync(Portal(user, worktree), Ct), "https://billing.stripe.com/p/session/fake");

        var form = fake.SingleForm(CheckoutPath);
        Assert.Equal($"{worktree}/subscription/success?session_id={{CHECKOUT_SESSION_ID}}", form["success_url"]);
        Assert.Equal($"{worktree}/subscription/canceled", form["cancel_url"]);
        Assert.Equal($"{worktree}/settings", fake.SingleForm(PortalPath)["return_url"]);
    }

    [Fact]
    public async Task CheckoutAndPortal_WithoutAConfiguredWebBase_Answer503AndNeverCallStripe()
    {
        var host = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:WebBaseUrl"] = "",
            // Even an origin on the allow-list is refused: with no canonical base there is
            // nothing to fall back to for every other caller, so the setup is broken.
            ["Stripe:AllowedReturnOrigins:0"] = "https://localhost:4310",
        })));
        var client = host.CreateClient();
        var fake = new FakeStripe();
        using var _ = fake.Install();
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.Active);

        var checkout = await client.SendAsync(Checkout(user, "https://localhost:4310"), Ct);
        var portal = await client.SendAsync(Portal(user, "https://evil.example"), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, checkout.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, portal.StatusCode);
        Assert.Empty(fake.Requests);
    }
}
