using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Webhooks;

namespace PrintLogApi.IntegrationTests.Webhooks;

/// <summary>
/// A host with webhooks configured (#235): an encryption key, DNS that answers a public address
/// for any host, deliveries routed to <see cref="Receiver"/> instead of the network, no retry
/// waits, and no background worker, so a test delivers what is queued with
/// <see cref="DeliverPendingAsync"/> and asserts on a settled state.
/// </summary>
public class WebhookTestFactory : CustomWebApplicationFactory
{
    public const string TestKey = "d2ViaG9vay1pbnRlZ3JhdGlvbi10ZXN0LWtleS0wMTIzNDU2Nzg5YWJjZA==";

    public FakeWebhookHostResolver Resolver { get; } = new() { Default = [IPAddress.Parse("93.184.215.14")] };

    public RecordingWebhookReceiver Receiver { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Webhooks:SecretEncryptionKeys:0"] = TestKey,
            ["Webhooks:RetryDelays:0"] = "00:00:00",
            ["Webhooks:RetryDelays:1"] = "00:00:00",
            ["Webhooks:RetryDelays:2"] = "00:00:00",
            ["Webhooks:MaxConsecutiveFailures"] = "3",
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IWebhookHostResolver>();
            services.AddSingleton<IWebhookHostResolver>(Resolver);
            services.AddHttpClient(WebhookSender.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Receiver);
            foreach (var worker in services.Where(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(WebhookDeliveryService)).ToList())
            {
                services.Remove(worker);
            }
        });
    }

    /// <summary>Delivers every queued event, in order, the way the background worker would.</summary>
    public async Task<int> DeliverPendingAsync(CancellationToken ct)
    {
        var queue = Services.GetRequiredService<WebhookEventQueue>();
        var delivered = 0;
        while (queue.Reader.TryRead(out var webhookEvent))
        {
            using var scope = Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>().ProcessAsync(webhookEvent, ct);
            delivered++;
        }
        return delivered;
    }

    /// <summary>Drops whatever earlier tests left queued, so a test sees only its own events.</summary>
    public void DiscardPending()
    {
        var queue = Services.GetRequiredService<WebhookEventQueue>();
        while (queue.Reader.TryRead(out _)) { }
    }

    public async Task<User> CreateUserAsync(bool pro = false)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|webhooks-{Guid.NewGuid():N}",
            DisplayName = "Webhooks",
            ViewStatus = User.ProfileViewStatus.Private,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        if (pro)
        {
            db.Subscriptions.Add(new Subscription
            {
                UserId = user.Id,
                Status = SubscriptionStatus.Active,
                Plan = SubscriptionPlan.ProMonthly,
                StripeCustomerId = $"cus_{user.Id}",
                StripeSubscriptionId = $"sub_{user.Id}",
                CreatedById = user.Id,
                UpdatedById = user.Id,
            });
            await db.SaveChangesAsync();
        }
        return user;
    }

    public async Task<long> CreatePrinterAsync(User user, string name = "Voron 2.4")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var printer = new Printer { Name = name, UserId = user.Id, IsActive = true, CategoryNickname = "FDM" };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();
        return printer.Id;
    }
}

/// <summary>
/// Stands in for the receiving end. Requests are recorded per host, and each host answers from its
/// own script (200 when the script is empty), so tests that use their own host never see each
/// other's deliveries.
/// </summary>
public sealed class RecordingWebhookReceiver : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<HttpResponseMessage>>> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ReceivedWebhook>> _received = new(StringComparer.OrdinalIgnoreCase);

    public void Respond(string host, params HttpStatusCode[] statuses)
    {
        var script = _scripts.GetOrAdd(host, _ => new());
        foreach (var status in statuses)
        {
            script.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent("response body") });
        }
    }

    public void Fail(string host, int times, Func<Exception> exception)
    {
        var script = _scripts.GetOrAdd(host, _ => new());
        for (var i = 0; i < times; i++)
        {
            script.Enqueue(() => throw exception());
        }
    }

    public IReadOnlyList<ReceivedWebhook> Received(string host) =>
        _received.TryGetValue(host, out var queue) ? queue.ToList() : [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri!.Host;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        _received.GetOrAdd(host, _ => new()).Enqueue(new ReceivedWebhook(request.Method, request.RequestUri, headers, body));

        if (_scripts.TryGetValue(host, out var script) && script.TryDequeue(out var next))
        {
            return next();
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok", Encoding.UTF8) };
    }
}

public sealed record ReceivedWebhook(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string Body);
