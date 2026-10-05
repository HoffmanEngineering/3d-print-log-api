using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Outbox;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Tokens;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.IntegrationTests.Email.Campaigns;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace PrintLogApi.IntegrationTests.Email;

/// <summary>The registered campaigns, evaluator and dispatcher working together, with only the transport faked.</summary>
public class EmailPipelineEndToEndTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string Key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    private readonly CustomWebApplicationFactory _factory;

    public EmailPipelineEndToEndTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EmailOptions ActiveOptions(IEnumerable<IEmailCampaign> campaigns)
    {
        var options = new EmailOptions
        {
            Enabled = true,
            HoldoutPercent = 0,
            MaxSendsPerSecond = 1000,
            Notice = new NoticeOptions { Required = false },
            UnsubscribeSigningKeys = [Key],
            SuppressionPeppers = [Key],
            WebBaseUrl = "https://www.3dprintlog.test",
            ApiBaseUrl = "https://api.3dprintlog.test",
            PostalAddress = "PO Box 0, Testville, USA",
        };
        foreach (var campaign in campaigns)
        {
            options.Campaigns[campaign.Name] = new CampaignOptions { Enabled = true };
        }

        return options;
    }

    [Fact]
    public void AllThreeCampaignsAreRegistered()
    {
        using var scope = _factory.Services.CreateScope();
        var names = scope.ServiceProvider.GetServices<IEmailCampaign>().Select(c => c.Name).Order().ToList();

        Assert.Equal(["monthly-recap", "onboarding", "printer-silent"], names);
    }

    [Fact]
    public async Task RecapDue_EvaluatorThenDispatcher_SendsOneMessageWithOneClickHeaders()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<PrintLogContext>();
        await db.EmailOutbox.ExecuteDeleteAsync(Ct);

        var user = await EmailTestData.CreateUserAsync(db, createdDate: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var printer = await CampaignTestData.PrinterAsync(db, user.Id);
        await CampaignTestData.PrintAsync(db, user.Id, printer.Id, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));

        // 09:00 on Dec 1 in America/Chicago, the default zone.
        var clock = new SettableTimeProvider(new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero));
        var campaigns = sp.GetServices<IEmailCampaign>().ToList();
        var options = MsOptions.Create(ActiveOptions(campaigns));
        var telemetry = new TelemetryClient(new TelemetryConfiguration());
        var transport = new FakeEmailTransport();

        await new CampaignEvaluator(db, campaigns, options, telemetry).RunOnceAsync(clock.GetUtcNow(), Ct);
        await new EmailDispatcher(
                db,
                campaigns,
                new EmailPreferenceService(db, telemetry),
                new EmailAddressHasher(options),
                transport,
                new EmailTokenService(options, clock),
                new EmailLinkBuilder(options),
                options,
                clock,
                telemetry)
            .RunOnceAsync(clock.GetUtcNow(), Ct);

        var message = Assert.Single(transport.Sent, m => m.ToAddress == user.Email);
        Assert.StartsWith("Your ", message.Subject);
        Assert.Equal("monthly-recap", message.Campaign);
        Assert.StartsWith("<https://api.3dprintlog.test/", message.Headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", message.Headers["List-Unsubscribe-Post"]);
    }

    [Fact]
    public async Task HostedServices_OneTickEach_ResolveAndRunWithoutErrors()
    {
        var logs = new RecordingLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs));
        var options = MsOptions.Create(new EmailOptions { Enabled = true });

        var evaluatorScopes = new SignalingScopeFactory(_factory.Services.GetRequiredService<IServiceScopeFactory>());
        var dispatcherScopes = new SignalingScopeFactory(_factory.Services.GetRequiredService<IServiceScopeFactory>());
        using var evaluator = new CampaignEvaluatorService(evaluatorScopes, options, new ImmediateTimeProvider(), loggers.CreateLogger<CampaignEvaluatorService>());
        using var dispatcher = new EmailDispatcherService(dispatcherScopes, options, new ImmediateTimeProvider(), loggers.CreateLogger<EmailDispatcherService>());

        await evaluator.StartAsync(Ct);
        await dispatcher.StartAsync(Ct);
        await evaluatorScopes.FirstScopeDisposed.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await dispatcherScopes.FirstScopeDisposed.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await evaluator.StopAsync(Ct);
        await dispatcher.StopAsync(Ct);

        Assert.Empty(logs.Errors);
    }

    /// <summary>Fires every timer at once, then hourly: the services' start delays collapse and exactly one tick runs during a test.</summary>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => base.CreateTimer(callback, state, TimeSpan.Zero, period == Timeout.InfiniteTimeSpan ? period : TimeSpan.FromHours(1));
    }

    /// <summary>Signals when the first scope a tick created is disposed, which is the end of that tick's work.</summary>
    private sealed class SignalingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstScopeDisposed => _disposed.Task;

        public IServiceScope CreateScope() => new SignalingScope(inner.CreateScope(), _disposed);

        private sealed class SignalingScope(IServiceScope inner, TaskCompletionSource disposed) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => inner.ServiceProvider;

            public void Dispose()
            {
                inner.Dispose();
                disposed.TrySetResult();
            }

            public async ValueTask DisposeAsync()
            {
                if (inner is IAsyncDisposable asyncInner)
                {
                    await asyncInner.DisposeAsync();
                }
                else
                {
                    inner.Dispose();
                }

                disposed.TrySetResult();
            }
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<string> Errors { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Recorder(this);

        public void Dispose()
        {
        }

        private sealed class Recorder(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                {
                    lock (owner.Errors)
                    {
                        owner.Errors.Add($"{formatter(state, exception)} {exception}");
                    }
                }
            }
        }
    }
}
