using System.Net;
using System.Text;
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
}
