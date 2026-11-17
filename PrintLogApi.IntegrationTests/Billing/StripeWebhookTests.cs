using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http.Metadata;
using PrintLogApi.IntegrationTests.Webhooks;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Billing;

/// <summary>Stripe's inbound webhook: each handled event with a correct signature, and the guards around it.</summary>
[Collection(StripeClientCollection.Name)]
public class StripeWebhookTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    /// <summary><c>Stripe:WebhookSecret</c> in appsettings.IntegrationTesting.json.</summary>
    public const string WebhookSecret = "whsec_integration_testing";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Created before any test installs the fake, so building the host cannot replace it.
    private readonly HttpClient _client = factory.CreateClient();

    private static HttpRequestMessage Webhook(string json, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Subscription/webhook") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", signature);
        return request;
    }

    private static async Task AssertOk(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static object SubscriptionObject(User user, string status) => new
    {
        id = StripeTestData.SubscriptionId(user),
        @object = "subscription",
        customer = StripeTestData.CustomerId(user),
        status,
        cancel_at_period_end = false,
        items = new { @object = "list", data = Array.Empty<object>() },
    };

    private static string CheckoutCompleted(User user) => StripeTestData.EventJson("checkout.session.completed", new
    {
        id = "cs_completed",
        @object = "checkout.session",
        customer = StripeTestData.CustomerId(user),
        subscription = StripeTestData.SubscriptionId(user),
        mode = "subscription",
        status = "complete",
        metadata = new Dictionary<string, string> { ["userId"] = user.Id.ToString() },
    });

    #region Correctly signed events

    [Fact]
    public async Task CheckoutCompleted_ForAnActiveSubscription_GrantsProAndSendsTheActivatedNotice()
    {
        var fake = new FakeStripe { SubscriptionJson = id => FakeStripe.SubscriptionObjectJson(id, "active") };
        using var _ = fake.Install();
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.None);
        var json = CheckoutCompleted(user);

        await AssertOk(await _client.SendAsync(Webhook(json, StripeTestData.Sign(WebhookSecret, json)), Ct));

        var subscription = await StripeTestData.SubscriptionAsync(factory, user.Id);
        Assert.Equal(SubscriptionStatus.Active, subscription!.Status);
        Assert.Equal(SubscriptionPlan.ProMonthly, subscription.Plan);
        Assert.Equal(StripeTestData.SubscriptionId(user), subscription.StripeSubscriptionId);
        Assert.Equal(1, await StripeTestData.NotificationCountAsync(factory, user.Id, NotificationType.SubscriptionActivated));
    }

    [Fact]
    public async Task SubscriptionUpdated_ToActive_GrantsPro()
    {
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.Canceled);
        var json = StripeTestData.EventJson("customer.subscription.updated", SubscriptionObject(user, "active"));

        await AssertOk(await _client.SendAsync(Webhook(json, StripeTestData.Sign(WebhookSecret, json)), Ct));

        Assert.Equal(SubscriptionStatus.Active, (await StripeTestData.SubscriptionAsync(factory, user.Id))!.Status);
    }

    [Fact]
    public async Task SubscriptionDeleted_EndsPro()
    {
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.Active);
        var json = StripeTestData.EventJson("customer.subscription.deleted", SubscriptionObject(user, "canceled"));

        await AssertOk(await _client.SendAsync(Webhook(json, StripeTestData.Sign(WebhookSecret, json)), Ct));

        var subscription = await StripeTestData.SubscriptionAsync(factory, user.Id);
        Assert.Equal(SubscriptionStatus.Canceled, subscription!.Status);
        Assert.Equal(SubscriptionPlan.Free, subscription.Plan);
    }

    [Fact]
    public async Task PaymentFailed_MarksTheSubscriptionPastDue()
    {
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.Active);
        var json = StripeTestData.EventJson("invoice.payment_failed", new { id = "in_failed", @object = "invoice", customer = StripeTestData.CustomerId(user) });

        await AssertOk(await _client.SendAsync(Webhook(json, StripeTestData.Sign(WebhookSecret, json)), Ct));

        Assert.Equal(SubscriptionStatus.PastDue, (await StripeTestData.SubscriptionAsync(factory, user.Id))!.Status);
    }

    [Fact]
    public async Task AnEventSignedWithAnotherKey_IsRejected()
    {
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.Canceled);
        var json = StripeTestData.EventJson("customer.subscription.updated", SubscriptionObject(user, "active"));

        var response = await _client.SendAsync(Webhook(json, StripeTestData.Sign("whsec_someone_else", json)), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SubscriptionStatus.Canceled, (await StripeTestData.SubscriptionAsync(factory, user.Id))!.Status);
    }

    #endregion

    #region checkout.session.completed reads the subscription's live status (W3b)

    /// <summary>
    /// A late or replayed <c>checkout.session.completed</c> for a subscription Stripe has since
    /// ended must not grant Pro: nothing would ever take it back.
    /// </summary>
    [Theory]
    [InlineData("canceled", SubscriptionStatus.Canceled)]
    [InlineData("incomplete_expired", SubscriptionStatus.None)]
    [InlineData("incomplete", SubscriptionStatus.None)]
    [InlineData("past_due", SubscriptionStatus.PastDue)]
    [InlineData("unpaid", SubscriptionStatus.PastDue)]
    [InlineData("paused", SubscriptionStatus.None)]
    [InlineData("a_status_stripe_adds_later", SubscriptionStatus.None)]
    public async Task CheckoutCompleted_ForASubscriptionThatIsNoLongerActive_DoesNotGrantPro(string liveStatus, SubscriptionStatus expected)
    {
        var fake = new FakeStripe { SubscriptionJson = id => FakeStripe.SubscriptionObjectJson(id, liveStatus) };
        using var _ = fake.Install();
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.Canceled);
        var json = CheckoutCompleted(user);

        await AssertOk(await _client.SendAsync(Webhook(json, StripeTestData.Sign(WebhookSecret, json)), Ct));

        Assert.Contains(fake.Requests, r => r.Path == $"/v1/subscriptions/{StripeTestData.SubscriptionId(user)}");
        Assert.Equal(expected, (await StripeTestData.SubscriptionAsync(factory, user.Id))!.Status);
        Assert.Equal(0, await StripeTestData.NotificationCountAsync(factory, user.Id, NotificationType.SubscriptionActivated));
    }

    [Fact]
    public async Task CheckoutCompleted_ForATrialingSubscription_GrantsPro()
    {
        var fake = new FakeStripe { SubscriptionJson = id => FakeStripe.SubscriptionObjectJson(id, "trialing") };
        using var _ = fake.Install();
        var user = await StripeTestData.UserAsync(factory, SubscriptionStatus.None);
        var json = CheckoutCompleted(user);

        await AssertOk(await _client.SendAsync(Webhook(json, StripeTestData.Sign(WebhookSecret, json)), Ct));

        Assert.Equal(SubscriptionStatus.Active, (await StripeTestData.SubscriptionAsync(factory, user.Id))!.Status);
        Assert.Equal(1, await StripeTestData.NotificationCountAsync(factory, user.Id, NotificationType.SubscriptionActivated));
    }

    #endregion

    #region A blank webhook secret refuses every event (W3a)

    public sealed class BlankSecretFactory : CustomWebApplicationFactory
    {
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        }
    }

    /// <summary>
    /// Stripe.net accepts a payload signed with an empty key when the configured secret is empty,
    /// so a missing <c>Stripe:WebhookSecret</c> would let anyone grant themselves Pro or downgrade
    /// any customer. The endpoint refuses instead, before it reads or checks the body.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankSecret_RefusesEvenAPayloadSignedWithThatBlankKey(string blank)
    {
        using var host = new BlankSecretFactory();
        var client = host.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stripe:WebhookSecret"] = blank,
        }))).CreateClient();
        var canceled = await StripeTestData.UserAsync(host, SubscriptionStatus.Canceled);
        var victim = await StripeTestData.UserAsync(host, SubscriptionStatus.Active);
        const string marker = "body-marker-that-must-not-be-logged";
        var regrant = StripeTestData.EventJson("customer.subscription.updated", new
        {
            id = StripeTestData.SubscriptionId(canceled),
            @object = "subscription",
            customer = StripeTestData.CustomerId(canceled),
            status = "active",
            cancel_at_period_end = false,
            description = marker,
            items = new { @object = "list", data = Array.Empty<object>() },
        });
        var downgrade = StripeTestData.EventJson("invoice.payment_failed", new { id = "in_forged", @object = "invoice", customer = StripeTestData.CustomerId(victim) });

        var first = await client.SendAsync(Webhook(regrant, StripeTestData.Sign(blank, regrant)), Ct);
        var second = await client.SendAsync(Webhook(downgrade, StripeTestData.Sign(blank, downgrade)), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(SubscriptionStatus.Canceled, (await StripeTestData.SubscriptionAsync(host, canceled.Id))!.Status);
        Assert.Equal(SubscriptionStatus.Active, (await StripeTestData.SubscriptionAsync(host, victim.Id))!.Status);

        // One line per refused request, naming the setting, and never the body.
        var refusals = host.Logs.Messages.Where(m => m.Contains("Stripe:WebhookSecret", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, refusals.Count);
        Assert.DoesNotContain(host.Logs.Messages, m => m.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>The test host, but in Production with no webhook secret.</summary>
    public sealed class ProductionWithoutSecretFactory : CustomWebApplicationFactory
    {
        public StripeOptionsTests.LevelCapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stripe:WebhookSecret"] = "",
            }));
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        }
    }

    /// <summary>
    /// A missing secret in Production must not take the API down: the host starts, says so once
    /// at Critical, and the webhook fails closed.
    /// </summary>
    [Fact]
    public async Task Production_WithoutASecret_StartsLogsOneCriticalEntryAndRefusesWebhooks()
    {
        using var host = new ProductionWithoutSecretFactory();
        var client = host.CreateClient();
        var user = await StripeTestData.UserAsync(host, SubscriptionStatus.Canceled);
        var json = StripeTestData.EventJson("customer.subscription.updated", SubscriptionObject(user, "active"));

        var response = await client.SendAsync(Webhook(json, StripeTestData.Sign("", json)), Ct);
        var health = await client.GetAsync("/api/Subscription/me", Ct);

        Assert.Equal("Production", host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(SubscriptionStatus.Canceled, (await StripeTestData.SubscriptionAsync(host, user.Id))!.Status);
        var critical = Assert.Single(host.Logs.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains("Stripe:WebhookSecret is not configured", critical.Message);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, health.StatusCode);
    }

    #endregion

    #region Request size (W3c)

    /// <summary>
    /// The body is read before its signature can be checked, on an anonymous route with no rate
    /// limit, so its size is capped well below the server default (about 28.6 MiB). TestServer does
    /// not implement <c>IHttpMaxRequestBodySizeFeature</c>, so an oversized POST cannot be shown to
    /// fail here; this pins the limit Kestrel and IIS enforce, as the endpoint's metadata carries it.
    /// </summary>
    [Fact]
    public void TheWebhookEndpoint_LimitsItsBodyToOneMebibyte()
    {
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "api/Subscription/webhook");

        var limit = endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>();

        Assert.NotNull(limit);
        Assert.Equal(1024 * 1024, limit.MaxRequestBodySize);
    }

    #endregion
}
