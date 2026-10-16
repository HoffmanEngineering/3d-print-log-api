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
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"printlog-webhooks-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Webhooks:Timeout"] = "00:00:04",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<PrintLogContext>>();
                services.AddDbContext<PrintLogContext>((sp, options) =>
                {
                    options.UseSqlite($"Data Source={_path};Default Timeout=30");
                    options.AddAchievementInterceptors(sp);
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

    private async Task<(User User, long PrinterId, string Host)> SubscribeAsync(string prefix)
    {
        var user = await factory.CreateUserAsync();
        var printerId = await factory.CreatePrinterAsync(user);
        var host = $"{prefix}-{Guid.NewGuid():N}.example.test";
        var subscribe = new HttpRequestMessage(HttpMethod.Post, "/api/Webhooks")
        {
            Content = JsonContent.Create(new { url = $"https://{host}/api/webhook/x", events = new[] { "print.started" } }),
        };
        subscribe.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(subscribe, Ct)).StatusCode);
        return (user, printerId, host);
    }

    private async Task StartPrintAsync((User User, long PrinterId, string Host) a)
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/Prints")
        {
            Content = JsonContent.Create(new AddPrintDTO { Title = "Background", PrinterId = a.PrinterId, Status = PrintStatus.Printing, FilamentUsage = [] }),
        };
        create.Headers.Add(TestAuthHandler.TestUserIdHeader, a.User.OAuthUserId);
        Assert.True((await _client.SendAsync(create, Ct)).IsSuccessStatusCode);
    }

    private async Task<bool> WaitUntil(Func<bool> condition, TimeSpan within)
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
