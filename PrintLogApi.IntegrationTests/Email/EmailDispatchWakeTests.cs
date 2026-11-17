using PrintLogApi.Email;
using PrintLogApi.Email.Outbox;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace PrintLogApi.IntegrationTests.Email;

/// <summary>
/// The dispatcher polls rarely (every two minutes) to keep idle SQL load down on the S0
/// database, so anything that must go out promptly wakes it instead: a queued security notice
/// does not wait for the next poll.
/// </summary>
public class EmailDispatchWakeTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void DispatchInterval_DefaultsToTwoMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), new EmailOptions().DispatchInterval);
    }

    [Fact]
    public void TheSignal_IsOneSingletonForTheProcess()
    {
        Assert.Same(factory.Services.GetRequiredService<EmailDispatchSignal>(), factory.Services.GetRequiredService<EmailDispatchSignal>());
    }

    [Fact]
    public async Task Wake_RunsATick_WithoutWaitingForTheInterval()
    {
        var options = MsOptions.Create(new EmailOptions { Enabled = true, DispatchInterval = TimeSpan.FromHours(1) });
        var scopes = new CountingScopeFactory(factory.Services.GetRequiredService<IServiceScopeFactory>());
        var signal = new EmailDispatchSignal();
        using var loggers = LoggerFactory.Create(_ => { });
        using var dispatcher = new EmailDispatcherService(scopes, options, signal, new NoInitialDelayTimeProvider(), loggers.CreateLogger<EmailDispatcherService>());

        await dispatcher.StartAsync(Ct);
        await scopes.WaitForTicksAsync(1);

        signal.Wake();
        await scopes.WaitForTicksAsync(2);

        await dispatcher.StopAsync(Ct);
    }

    [Fact]
    public void WakesBeforeATickIsWaiting_AreNotLost_AndCoalesce()
    {
        var signal = new EmailDispatchSignal();
        signal.Wake();
        signal.Wake();

        Assert.True(signal.TryConsume());
        Assert.False(signal.TryConsume());
    }

    /// <summary>The ten-second start delay passes at once; the dispatch period is left as given (an hour here).</summary>
    private sealed class NoInitialDelayTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => base.CreateTimer(callback, state, period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : dueTime, period);
    }

    /// <summary>Counts finished ticks: each tick creates and disposes one scope.</summary>
    private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private readonly SemaphoreSlim _ticks = new(0);

        public async Task WaitForTicksAsync(int count)
        {
            for (var i = 0; i < count - _consumed; i++)
            {
                Assert.True(await _ticks.WaitAsync(TimeSpan.FromSeconds(10), Ct), "The dispatcher did not tick.");
            }

            _consumed = count;
        }

        private int _consumed;

        public IServiceScope CreateScope() => new CountingScope(inner.CreateScope(), _ticks);

        private sealed class CountingScope(IServiceScope inner, SemaphoreSlim ticks) : IServiceScope, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => inner.ServiceProvider;

            public void Dispose()
            {
                inner.Dispose();
                ticks.Release();
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

                ticks.Release();
            }
        }
    }
}
