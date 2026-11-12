using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Billing;

/// <summary>
/// Tests that replace <see cref="Stripe.StripeConfiguration.StripeClient"/>, a process-wide static,
/// run here and nowhere else, one at a time, so no other test can see the fake client.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StripeClientCollection
{
    public const string Name = "Stripe client (not parallel)";
}

/// <summary>
/// Stands in for api.stripe.com. Records each request Stripe would have received and answers from
/// a fixed route table. <see cref="Install"/> points the global Stripe client at it until disposed;
/// nothing ever reaches the real Stripe API.
/// </summary>
public sealed class FakeStripe : HttpMessageHandler
{
    public ConcurrentQueue<(HttpMethod Method, string Path, string Body)> Requests { get; } = new();

    /// <summary>The body of <c>GET /v1/subscriptions/{id}</c>, given the id.</summary>
    public Func<string, string>? SubscriptionJson { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request.Method, path, body));
        var json = path switch
        {
            "/v1/customers" => """{"id":"cus_fake_new","object":"customer"}""",
            "/v1/checkout/sessions" => """{"id":"cs_test_fake","object":"checkout.session","url":"https://checkout.stripe.com/c/pay/cs_test_fake"}""",
            "/v1/billing_portal/sessions" => """{"id":"bps_fake","object":"billing_portal.session","url":"https://billing.stripe.com/p/session/fake"}""",
            _ when path.StartsWith("/v1/subscriptions/", StringComparison.Ordinal) && SubscriptionJson is not null
                => SubscriptionJson(path["/v1/subscriptions/".Length..]),
            _ => null,
        };
        return json is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":{"message":"unexpected request"}}""", Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>Routes Stripe's outbound calls here until the returned scope is disposed.</summary>
    public IDisposable Install()
    {
        Stripe.StripeConfiguration.StripeClient = new Stripe.StripeClient(
            "sk_test_fake", httpClient: new Stripe.SystemNetHttpClient(new HttpClient(this)));
        return new Restore();
    }

    /// <summary>The form Stripe received at <paramref name="path"/>, which must have been called once.</summary>
    public System.Collections.Specialized.NameValueCollection SingleForm(string path) =>
        System.Web.HttpUtility.ParseQueryString(Assert.Single(Requests, r => r.Path == path).Body);

    public static string SubscriptionObjectJson(string id, string status) => $$$"""
        {"id":"{{{id}}}","object":"subscription","status":"{{{status}}}","cancel_at_period_end":false,"customer":"cus_x",
         "items":{"object":"list","data":[{"id":"si_1","object":"subscription_item",
           "price":{"id":"price_1T5tO0JyHW0UWXhycxb6dpjw","object":"price"},
           "current_period_start":1760000000,"current_period_end":1762592000}]}}
        """;

    private sealed class Restore : IDisposable
    {
        // Null makes Stripe.net build its default client from StripeConfiguration.ApiKey again on
        // next use, which is the state every other test runs in.
        public void Dispose() => Stripe.StripeConfiguration.StripeClient = null;
    }
}

/// <summary>Seeds users and signs Stripe events the way Stripe signs them.</summary>
internal static class StripeTestData
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<User> UserAsync(CustomWebApplicationFactory factory, SubscriptionStatus? status = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User { OAuthUserId = $"auth0|stripe-{Guid.NewGuid():N}", DisplayName = "Stripe test", ViewStatus = User.ProfileViewStatus.Private };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        if (status is { } s)
        {
            db.Subscriptions.Add(new Subscription
            {
                UserId = user.Id,
                Status = s,
                Plan = s == SubscriptionStatus.Active ? SubscriptionPlan.ProMonthly : SubscriptionPlan.Free,
                StripeCustomerId = CustomerId(user),
                StripeSubscriptionId = SubscriptionId(user),
                CreatedById = user.Id,
                UpdatedById = user.Id,
            });
            await db.SaveChangesAsync(Ct);
        }
        return user;
    }

    public static string CustomerId(User user) => $"cus_st_{user.Id}";

    public static string SubscriptionId(User user) => $"sub_st_{user.Id}";

    public static async Task<Subscription?> SubscriptionAsync(CustomWebApplicationFactory factory, long userId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>().Subscriptions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.UserId == userId, Ct);
    }

    public static async Task<int> NotificationCountAsync(CustomWebApplicationFactory factory, long userId, NotificationType type)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>().Notifications
            .CountAsync(n => n.UserId == userId && n.Type == type, Ct);
    }

    public static string Sign(string secret, string json)
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var v1 = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{json}")));
        return $"t={t},v1={v1}";
    }

    public static string EventJson(string type, object dataObject) => JsonSerializer.Serialize(new
    {
        id = $"evt_{Guid.NewGuid():N}",
        @object = "event",
        api_version = Stripe.StripeConfiguration.ApiVersion,
        type,
        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        livemode = false,
        pending_webhooks = 1,
        request = new { id = (string?)null, idempotency_key = (string?)null },
        data = new { @object = dataObject },
    });
}
