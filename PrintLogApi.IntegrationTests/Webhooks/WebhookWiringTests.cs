using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PrintLogApi.Webhooks;
using Xunit;

namespace PrintLogApi.IntegrationTests.Webhooks;

/// <summary>
/// The production registration of the webhook HTTP client (#235), with nothing overridden: the
/// guarded primary handler is really in place, and a delivery leaks neither its URL path (into
/// telemetry or logs) nor trace headers (to the receiver).
/// </summary>
/// <remarks>
/// Not parallel: the telemetry half reads what the real Application Insights dependency module
/// records, and with other test hosts running at the same time the outbound request's dependency
/// never reached this host's channel (seen in the full suite, never alone).
/// </remarks>
[Collection(NonParallelWebhookWiring.Name)]
public class WebhookWiringTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A host with a key and the production handler; private addresses stay blocked.</summary>
    public class ProductionWiringFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Webhooks:SecretEncryptionKeys:0"] = WebhookTestFactory.TestKey,
            }));
        }
    }

    /// <summary>
    /// The production handler, built from a guard that admits loopback, so a delivery can complete
    /// against a local server and its telemetry, logs and headers can be inspected.
    /// </summary>
    public sealed class LoopbackWiringFactory : ProductionWiringFactory
    {
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Webhooks:Timeout"] = "00:00:01",
            }));
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<WebhookAddressGuard>();
                services.AddSingleton(sp => new WebhookAddressGuard(sp.GetRequiredService<IWebhookHostResolver>(), allowPrivateAddresses: true));
            });
        }
    }

    [Fact]
    public async Task TheRegisteredClient_RefusesLoopback()
    {
        await using var factory = new ProductionWiringFactory();
        await using var server = new LoopbackHttpServer(LoopbackHttpServer.Ok());
        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(WebhookSender.HttpClientName);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{server.Port}/", Ct));

        Assert.IsType<WebhookAddressBlockedException>(ex.InnerException);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task TheHandler_AllowsAsManyConnectionsToAHost_AsTheSchedulerDoes()
    {
        // A tighter pool would be a second, hidden host cap: hung requests to a shared host
        // (Nabu Casa) would hold every connection and queue everyone else inside the handler.
        await using var factory = new ProductionWiringFactory();
        HttpMessageHandler? handler = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(WebhookSender.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler;
        }

        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebhookOptions>>().Value;
        Assert.True(sockets.MaxConnectionsPerServer >= options.MaxInFlightPerHost,
            $"{sockets.MaxConnectionsPerServer} connections for {options.MaxInFlightPerHost} attempts");
    }

    [Fact]
    public async Task ADelivery_SendsNoTraceHeaders_AndKeepsItsPathOutOfTelemetryAndLogs()
    {
        await using var factory = new LoopbackWiringFactory();
        await using var server = new LoopbackHttpServer(LoopbackHttpServer.Ok());
        var sender = factory.Services.GetRequiredService<WebhookSender>();
        var url = new Uri($"http://127.0.0.1:{server.Port}/api/webhook/SECRET-HOOK-ID");
        factory.Telemetry.Clear();

        WebhookAttemptResult result;
        using (var activity = new Activity("test-request").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            result = await sender.SendAsync(url, "whsec_wiring",
                new WebhookMessage("webhook.test", "evt_wiring", Encoding.UTF8.GetBytes("{}")), attempt: 1, Ct);
        }

        Assert.True(result.Succeeded, result.Result);
        var head = Assert.Single(server.Requests);
        Assert.DoesNotContain("traceparent", head, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracestate", head, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Request-Id", head, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Request-Context", head, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("baggage", head, StringComparison.OrdinalIgnoreCase);

        var dependency = Assert.Single(factory.Telemetry.Items.OfType<DependencyTelemetry>(), d => d.Target?.Contains("127.0.0.1") == true);
        Assert.DoesNotContain("SECRET", dependency.Data);
        Assert.DoesNotContain("SECRET", dependency.Name);
        Assert.DoesNotContain(factory.Logs.Messages, m => m.Contains("SECRET", StringComparison.Ordinal));
        AssertNoSecretInAnyTelemetry(factory);
    }

    [Fact]
    public async Task ARefusedPort_KeepsItsPathOutOfTelemetryAndLogs()
    {
        await using var factory = new LoopbackWiringFactory();
        var sender = factory.Services.GetRequiredService<WebhookSender>();
        factory.Telemetry.Clear();

        var result = await sender.SendAsync(new Uri("http://127.0.0.1:25/api/webhook/SECRET-HOOK-ID"), "whsec_wiring",
            new WebhookMessage("webhook.test", "evt_refused", Encoding.UTF8.GetBytes("{}")), attempt: 1, Ct);

        Assert.Equal(WebhookDeliveryResults.BlockedAddress, result.Result);
        Assert.Contains(factory.Telemetry.Items.OfType<DependencyTelemetry>(), d => d.Success == false);
        Assert.DoesNotContain(factory.Logs.Messages, m => m.Contains("SECRET", StringComparison.Ordinal));
        AssertNoSecretInAnyTelemetry(factory);
    }

    [Fact]
    public async Task ATimeout_KeepsItsPathOutOfTelemetryAndLogs()
    {
        await using var factory = new LoopbackWiringFactory();
        // Accepts connections (the OS completes them) and never answers.
        var silent = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)silent.LocalEndpoint).Port;
            var sender = factory.Services.GetRequiredService<WebhookSender>();
            factory.Telemetry.Clear();

            var result = await sender.SendAsync(new Uri($"http://127.0.0.1:{port}/api/webhook/SECRET-HOOK-ID"), "whsec_wiring",
                new WebhookMessage("webhook.test", "evt_timeout", Encoding.UTF8.GetBytes("{}")), attempt: 1, Ct);

            Assert.Equal(WebhookDeliveryResults.Timeout, result.Result);
            Assert.Contains(factory.Telemetry.Items.OfType<DependencyTelemetry>(), d => d.Target?.Contains("127.0.0.1") == true);
            Assert.DoesNotContain(factory.Logs.Messages, m => m.Contains("SECRET", StringComparison.Ordinal));
            AssertNoSecretInAnyTelemetry(factory);
        }
        finally
        {
            silent.Stop();
        }
    }

    /// <summary>Every recorded item, serialized as it would be sent, carries no part of the URL path.</summary>
    private static void AssertNoSecretInAnyTelemetry(LoopbackWiringFactory factory)
    {
        var items = factory.Telemetry.Items.ToList();
        var sent = Encoding.UTF8.GetString(
            Microsoft.ApplicationInsights.Extensibility.Implementation.JsonSerializer.Serialize(items, compress: false));
        Assert.DoesNotContain("SECRET", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/webhook", sent, StringComparison.Ordinal);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NonParallelWebhookWiring
{
    public const string Name = "Webhook wiring (not parallel)";
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _messages);

    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue($"{category}: {formatter(state, exception)}");
    }
}
