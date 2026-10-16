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

    /// <summary>
    /// Delivers every queued event, in order, the way the background worker would: each
    /// subscription's delivery runs attempt by attempt until it is done, with the retry waits
    /// skipped.
    /// </summary>
    public async Task<int> DeliverPendingAsync(CancellationToken ct)
    {
        var queue = Services.GetRequiredService<WebhookEventQueue>();
        var delivered = 0;
        while (queue.Reader.TryRead(out var webhookEvent))
        {
            IReadOnlyList<WebhookDelivery> deliveries;
            using (var scope = Services.CreateScope())
            {
                deliveries = await scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>().PrepareAsync(webhookEvent, ct);
            }
            foreach (var first in deliveries)
            {
                WebhookDelivery? delivery = first;
                while (delivery is not null)
                {
                    using var scope = Services.CreateScope();
                    var outcome = await scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>().AttemptAsync(delivery, ct);
                    delivery = outcome.Next;
                }
            }
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
/// own script (then its default, 200 unless changed), so tests that use their own host never see
/// each other's deliveries. It also counts requests in flight per host.
/// </summary>
public sealed class RecordingWebhookReceiver : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<CancellationToken, Task<HttpResponseMessage>>>> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> _defaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ReceivedWebhook>> _received = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int[]> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("response body") };

    public void Respond(string host, params HttpStatusCode[] statuses)
    {
        foreach (var status in statuses)
        {
            Script(host, _ => Task.FromResult(Status(status)));
        }
    }

    /// <summary>Answers the next request with whatever <paramref name="respond"/> does.</summary>
    public void Respond(string host, Func<HttpResponseMessage> respond) => Script(host, _ => Task.FromResult(respond()));

    public void Fail(string host, int times, Func<Exception> exception)
    {
        for (var i = 0; i < times; i++)
        {
            Script(host, _ => throw exception());
        }
    }

    /// <summary>Every request to <paramref name="host"/> waits until the sender gives up.</summary>
    public void Hang(string host) => _defaults[host] = async ct =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        return Status(HttpStatusCode.OK);
    };

    /// <summary>Every request to <paramref name="host"/> takes <paramref name="delay"/>, then succeeds.</summary>
    public void Delay(string host, TimeSpan delay) => _defaults[host] = async ct =>
    {
        await Task.Delay(delay, ct);
        return Status(HttpStatusCode.OK);
    };

    public IReadOnlyList<ReceivedWebhook> Received(string host) =>
        _received.TryGetValue(host, out var queue) ? queue.ToList() : [];

    /// <summary>The most requests to <paramref name="host"/> that were in flight at once.</summary>
    public int MaxInFlight(string host) => _inFlight.TryGetValue(host, out var counts) ? Volatile.Read(ref counts[1]) : 0;

    private void Script(string host, Func<CancellationToken, Task<HttpResponseMessage>> respond) =>
        _scripts.GetOrAdd(host, _ => new()).Enqueue(respond);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri!.Host;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        _received.GetOrAdd(host, _ => new()).Enqueue(new ReceivedWebhook(request.Method, request.RequestUri, headers, body));

        var counts = _inFlight.GetOrAdd(host, _ => new int[2]);
        var now = Interlocked.Increment(ref counts[0]);
        int max;
        while (now > (max = Volatile.Read(ref counts[1])) && Interlocked.CompareExchange(ref counts[1], now, max) != max) { }
        try
        {
            if (_scripts.TryGetValue(host, out var script) && script.TryDequeue(out var next))
            {
                return await next(cancellationToken);
            }
            if (_defaults.TryGetValue(host, out var fallback))
            {
                return await fallback(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok", Encoding.UTF8) };
        }
        finally
        {
            Interlocked.Decrement(ref counts[0]);
        }
    }
}

public sealed record ReceivedWebhook(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string Body);
