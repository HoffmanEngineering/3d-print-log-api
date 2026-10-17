using System.Diagnostics;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PrintLogApi.Achievements.Triggers;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Webhooks;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Webhooks;

/// <summary>
/// The background worker itself (#235): it drains the queue, runs deliveries side by side, keeps a
/// blackholed receiver from holding up anyone else, and stops promptly.
/// </summary>
public class WebhookDeliveryServiceTests(WebhookDeliveryServiceTests.Factory factory) : IClassFixture<WebhookDeliveryServiceTests.Factory>
{
    /// <summary>
    /// The webhook host with its background worker put back, and a short attempt timeout. Its
    /// database is a SQLite file rather than the suite's one shared in-memory connection: that
    /// connection is not thread-safe, and here the workers query it while requests are running.
    /// </summary>
    public sealed class Factory : WebhookTestFactory
    {
        /// <summary>Extra settings for a host of its own; read when the host first starts.</summary>
        public IReadOnlyDictionary<string, string?>? Settings { get; init; }

        /// <summary>Extra EF interceptors for a host of its own.</summary>
        public IInterceptor[] Interceptors { get; init; } = [];

        private readonly string _path = Path.Combine(Path.GetTempPath(), $"printlog-webhooks-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Webhooks:Timeout"] = "00:00:04",
                // Real waits between retries (the base factory skips them), so order can be seen.
                ["Webhooks:RetryDelays:0"] = "00:00:01",
                ["Webhooks:RetryDelays:1"] = "00:00:01",
                ["Webhooks:RetryDelays:2"] = "00:00:01",
            }));
            if (Settings is { } settings)
            {
                builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings));
            }
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<PrintLogContext>>();
                services.AddDbContext<PrintLogContext>((sp, options) =>
                {
                    options.UseSqlite($"Data Source={_path};Default Timeout=30");
                    options.AddAchievementInterceptors(sp);
                    options.AddInterceptors(Interceptors);
                    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
                });
                services.AddHostedService<WebhookDeliveryService>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                SqliteConnection.ClearAllPools();
                try { System.IO.File.Delete(_path); } catch (IOException) { }
            }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HttpClient _client = factory.CreateClient();

    private Task<(User User, long PrinterId, string Host)> SubscribeAsync(string prefix) =>
        SubscribeAsync(factory, _client, prefix);

    /// <summary>A user (Pro, so several webhooks fit) with a printer and one webhook.</summary>
    private static async Task<(User User, long PrinterId, string Host)> SubscribeAsync(
        Factory host, HttpClient client, string prefix, string path = "/api/webhook/x", string[]? events = null, User? user = null)
    {
        user ??= await host.CreateUserAsync(pro: true);
        var printerId = await host.CreatePrinterAsync(user);
        var target = prefix.Contains('.') ? prefix : $"{prefix}-{Guid.NewGuid():N}.example.test";
        await SubscribeUrlAsync(client, user, $"https://{target}{path}", events ?? ["print.started"]);
        return (user, printerId, target);
    }

    private static async Task<Guid> SubscribeUrlAsync(HttpClient client, User user, string url, string[] events)
    {
        var subscribe = new HttpRequestMessage(HttpMethod.Post, "/api/Webhooks") { Content = JsonContent.Create(new { url, events }) };
        subscribe.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        var response = await client.SendAsync(subscribe, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PrintLogApi.Models.DTOs.Webhook.WebhookSubscriptionCreatedDto>(Ct))!.Id;
    }

    private Task<long> StartPrintAsync((User User, long PrinterId, string Host) a) => StartPrintAsync(_client, a);

    private static async Task<long> StartPrintAsync(HttpClient client, (User User, long PrinterId, string Host) a)
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/Prints")
        {
            Content = JsonContent.Create(new AddPrintDTO { Title = "Background", PrinterId = a.PrinterId, Status = PrintStatus.Printing, FilamentUsage = [] }),
        };
        create.Headers.Add(TestAuthHandler.TestUserIdHeader, a.User.OAuthUserId);
        var response = await client.SendAsync(create, Ct);
        Assert.True(response.IsSuccessStatusCode);
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(Ct))!.Id;
    }

    private static async Task FinishPrintAsync(HttpClient client, User user, long printId)
    {
        var complete = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{printId}/complete")
        {
            Content = JsonContent.Create(new CompletePrintDto { Status = PrintStatus.Success, PrintTimeInSeconds = 60, FilamentUsage = [] }),
        };
        complete.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        Assert.True((await client.SendAsync(complete, Ct)).IsSuccessStatusCode);
    }

    private static WebhookSubscription? Stored(Factory host, Guid id)
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<PrintLogContext>().WebhookSubscriptions.AsNoTracking().FirstOrDefault(w => w.Id == id);
    }

    private static WebhookSubscription? StoredFor(Factory host, long userId)
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<PrintLogContext>().WebhookSubscriptions.AsNoTracking().FirstOrDefault(w => w.UserId == userId);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan within)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < within)
        {
            await Task.Delay(25, Ct);
        }
        return condition();
    }

    [Fact]
    public async Task AQueuedEvent_IsDeliveredInTheBackground()
    {
        var a = await SubscribeAsync("bg");

        await StartPrintAsync(a);

        Assert.True(await WaitUntil(() => factory.Receiver.Received(a.Host).Count == 1, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task Deliveries_RunSideBySide()
    {
        var users = new List<(User User, long PrinterId, string Host)>();
        for (var i = 0; i < 6; i++)
        {
            var a = await SubscribeAsync("parallel");
            factory.Receiver.Delay(a.Host, TimeSpan.FromSeconds(1));
            users.Add(a);
        }

        var clock = Stopwatch.StartNew();
        foreach (var a in users)
        {
            await StartPrintAsync(a);
        }

        Assert.True(await WaitUntil(() => users.All(a => factory.Receiver.Received(a.Host).Count == 1), TimeSpan.FromSeconds(15)));
        // One after another would take at least six seconds.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task ABlackholedReceiver_DoesNotHoldUpAnotherUser_AndGetsAtMostTwoConnections()
    {
        var stuck = await SubscribeAsync("blackhole");
        factory.Receiver.Hang(stuck.Host);
        for (var i = 0; i < 5; i++)
        {
            await StartPrintAsync(stuck);
        }
        Assert.True(await WaitUntil(() => factory.Receiver.Received(stuck.Host).Count >= 1, TimeSpan.FromSeconds(10)));

        var other = await SubscribeAsync("healthy");
        var clock = Stopwatch.StartNew();
        await StartPrintAsync(other);

        Assert.True(await WaitUntil(() => factory.Receiver.Received(other.Host).Count == 1, TimeSpan.FromSeconds(10)));
        // Well inside one blackholed attempt's timeout (4 s here), so it did not wait for a slot.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
        Assert.InRange(factory.Receiver.MaxInFlight(stuck.Host), 1, 2);
    }

    [Fact]
    public async Task BlackholedUsers_OnASharedHost_DoNotStarveAHealthyUserOnIt()
    {
        // Every Home Assistant Cloud user posts to the same host. Two users' hung instances must
        // not use up that host's share, or every other user there waits out their timeouts.
        var shared = $"hooks-{Guid.NewGuid():N}.example.test";
        foreach (var name in new[] { "stuck-a", "stuck-b" })
        {
            var stuck = await SubscribeAsync(factory, _client, shared, $"/{name}");
            factory.Receiver.HangUrl($"https://{shared}/{name}");
            for (var i = 0; i < 3; i++)
            {
                await StartPrintAsync(stuck);
            }
        }
        Assert.True(await WaitUntil(() => factory.Receiver.Received(shared).Count >= 2, TimeSpan.FromSeconds(10)));

        var healthy = await SubscribeAsync(factory, _client, shared, "/healthy");
        var clock = Stopwatch.StartNew();
        await StartPrintAsync(healthy);

        Assert.True(await WaitUntil(() => factory.Receiver.Received(shared).Any(r => r.Url.AbsolutePath == "/healthy"), TimeSpan.FromSeconds(10)));
        // Well inside one hung attempt's timeout (4 s here).
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task ASubscription_GetsItsEventsInOrder_EvenWhenTheFirstIsRetried()
    {
        var a = await SubscribeAsync(factory, _client, "order", events: ["print.started", "print.finished"]);
        factory.Receiver.Respond(a.Host, HttpStatusCode.ServiceUnavailable);

        var printId = await StartPrintAsync(a);
        await FinishPrintAsync(_client, a.User, printId);

        Assert.True(await WaitUntil(() => factory.Receiver.Received(a.Host).Count == 3, TimeSpan.FromSeconds(15)));
        Assert.Equal(
            ["print.started", "print.started", "print.finished"],
            factory.Receiver.Received(a.Host).Select(r => r.Headers["X-PrintLog-Event"]).ToArray());
    }

    [Fact]
    public async Task ADeliveryOverTheQueueCapacity_IsRecordedAsDropped()
    {
        await using var host = new Factory { Settings = new Dictionary<string, string?> { ["Webhooks:DeliveryQueueCapacity"] = "1" } };
        var client = host.CreateClient();
        var a = await SubscribeAsync(host, client, "full");
        host.Receiver.Hang(a.Host);

        await StartPrintAsync(client, a);
        Assert.True(await WaitUntil(() => host.Receiver.Received(a.Host).Count == 1, TimeSpan.FromSeconds(10)));
        await StartPrintAsync(client, a);

        Assert.True(await WaitUntil(() => StoredFor(host, a.User.Id)?.LastDeliveryResult == "dropped", TimeSpan.FromSeconds(3)),
            $"stored: {StoredFor(host, a.User.Id)?.LastDeliveryResult}");
        // Our capacity ran out, not the receiver: no failure is counted.
        Assert.Equal(0, StoredFor(host, a.User.Id)!.ConsecutiveFailures);
    }

    [Fact]
    public async Task ADeliveryThatWaitedTooLongForCapacity_IsRecordedAsDropped()
    {
        await using var host = new Factory
        {
            Settings = new Dictionary<string, string?>
            {
                ["Webhooks:MaxInFlightPerUser"] = "1",
                ["Webhooks:MaxDeliveryWait"] = "00:00:01",
                ["Webhooks:Timeout"] = "00:00:02",
            }
        };
        var client = host.CreateClient();
        // The user's one slot goes to the hung start; the finish, for another webhook, has to wait.
        var stuck = await SubscribeAsync(host, client, "slow", events: ["print.started"]);
        host.Receiver.Hang(stuck.Host);
        var waitingHost = $"waiting-{Guid.NewGuid():N}.example.test";
        var waitingId = await SubscribeUrlAsync(client, stuck.User, $"https://{waitingHost}/api/webhook/x", ["print.finished"]);

        var printId = await StartPrintAsync(client, stuck);
        Assert.True(await WaitUntil(() => host.Receiver.Received(stuck.Host).Count == 1, TimeSpan.FromSeconds(10)));
        await FinishPrintAsync(client, stuck.User, printId);

        Assert.True(await WaitUntil(() => Stored(host, waitingId)?.LastDeliveryResult is not null, TimeSpan.FromSeconds(10)));
        Assert.Equal("dropped", Stored(host, waitingId)!.LastDeliveryResult);
        Assert.Equal(0, Stored(host, waitingId)!.ConsecutiveFailures);
        Assert.Empty(host.Receiver.Received(waitingHost));
    }

    /// <summary>One user with a hung webhook holding the only slot, and nine more webhooks.</summary>
    private static async Task<(User User, long PrinterId, string Host)> NineDropsAsync(Factory host, HttpClient client)
    {
        var a = await SubscribeAsync(host, client, "holder");
        host.Receiver.Hang(a.Host);
        for (var i = 0; i < 9; i++)
        {
            await SubscribeUrlAsync(client, a.User, $"https://dropped-{i}-{Guid.NewGuid():N}.example.test/x", ["print.started"]);
        }
        return a;
    }

    [Fact]
    public async Task ManyDropsAtOnce_AreRecordedOneWriteAtATime()
    {
        var writes = new DropWrites(TimeSpan.FromMilliseconds(100));
        await using var host = new Factory
        {
            Settings = new Dictionary<string, string?> { ["Webhooks:DeliveryQueueCapacity"] = "1" },
            Interceptors = [writes],
        };
        var client = host.CreateClient();
        var a = await NineDropsAsync(host, client);

        // One event, ten webhooks: one takes the only slot and nine are dropped in the same pass.
        await StartPrintAsync(client, a);

        Assert.True(await WaitUntil(() => writes.Completed == 9, TimeSpan.FromSeconds(10)), $"{writes.Completed} written");
        Assert.Equal(1, writes.MaxConcurrent);
    }

    [Fact]
    public async Task Stopping_EndsTheDropRecorder_WithNoWriteLeftRunning()
    {
        var writes = new DropWrites(TimeSpan.FromSeconds(5));
        await using var host = new Factory
        {
            Settings = new Dictionary<string, string?> { ["Webhooks:DeliveryQueueCapacity"] = "1" },
            Interceptors = [writes],
        };
        var client = host.CreateClient();
        var a = await NineDropsAsync(host, client);
        await StartPrintAsync(client, a);
        Assert.True(await WaitUntil(() => writes.Started >= 1, TimeSpan.FromSeconds(10)));

        var worker = host.Services.GetServices<IHostedService>().OfType<WebhookDeliveryService>().Single();
        var clock = Stopwatch.StartNew();
        await worker.StopAsync(Ct);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
        Assert.Equal(0, writes.InFlight);
        var started = writes.Started;
        await Task.Delay(300, Ct);
        Assert.Equal(started, writes.Started);
    }

    /// <summary>
    /// Counts the drop writes (the UPDATE that sets "dropped") and how many run at once, each held
    /// for <paramref name="hold"/> so overlapping ones would show.
    /// </summary>
    private sealed class DropWrites(TimeSpan hold) : DbCommandInterceptor
    {
        private int _inFlight;
        private int _max;
        private int _started;
        private int _completed;

        public int InFlight => Volatile.Read(ref _inFlight);

        public int MaxConcurrent => Volatile.Read(ref _max);

        public int Started => Volatile.Read(ref _started);

        public int Completed => Volatile.Read(ref _completed);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("WebhookSubscriptions", StringComparison.Ordinal)
                || !(command.CommandText.Contains("dropped", StringComparison.Ordinal)
                    || command.Parameters.Cast<System.Data.Common.DbParameter>().Any(p => Equals(p.Value, "dropped"))))
            {
                return result;
            }
            Interlocked.Increment(ref _started);
            var now = Interlocked.Increment(ref _inFlight);
            int max;
            while (now > (max = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, max) != max) { }
            try
            {
                await Task.Delay(hold, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
            Interlocked.Increment(ref _completed);
            return result;
        }
    }

    [Fact]
    public async Task Stopping_DoesNotWaitOutABlackholedDelivery()
    {
        await using var host = new Factory();
        var client = host.CreateClient();
        var user = await host.CreateUserAsync();
        var printerId = await host.CreatePrinterAsync(user);
        var target = $"stop-{Guid.NewGuid():N}.example.test";
        host.Receiver.Hang(target);
        var subscribe = new HttpRequestMessage(HttpMethod.Post, "/api/Webhooks") { Content = JsonContent.Create(new { url = $"https://{target}/api/webhook/x" }) };
        subscribe.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(subscribe, Ct)).StatusCode);
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/Prints")
        {
            Content = JsonContent.Create(new AddPrintDTO { Title = "Stop", PrinterId = printerId, Status = PrintStatus.Printing, FilamentUsage = [] }),
        };
        create.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        Assert.True((await client.SendAsync(create, Ct)).IsSuccessStatusCode);
        Assert.True(await WaitUntil(() => host.Receiver.Received(target).Count == 1, TimeSpan.FromSeconds(10)));

        var worker = host.Services.GetServices<IHostedService>().OfType<WebhookDeliveryService>().Single();
        var clock = Stopwatch.StartNew();
        await worker.StopAsync(Ct);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
    }
}
