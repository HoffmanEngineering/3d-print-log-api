using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintLogApi.Models;
using PrintLogApi.Services;
using PrintLogApi.Services.UserDeletion;
using Xunit;
using static PrintLogApi.Models.User;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// Accounts a user deactivated are deleted by an in-process job once the grace period is over.
/// It replaced an Azure Function that called an anonymous endpoint (anyone could trigger the
/// delete). The test host removes the hosted job, so nothing here runs on its own timer: each test
/// drives <c>RunOnceAsync</c>, or starts its own instance on a clock it advances by hand.
/// </summary>
public class PendingUserDeletionJobTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Interval = TimeSpan.FromHours(2);

    #region Options and registration

    [Fact]
    public void Options_DefaultToEnabled_EveryTwoHours_LikeTheAzureFunction()
    {
        var options = new PendingUserDeletionOptions();

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromHours(2), options.RunInterval);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:05:00")]
    [InlineData("00:00:30")]
    [InlineData("1.00:00:01")]
    public void Validator_RejectsAnIntervalOutsideOneMinuteToOneDay(string interval)
    {
        var result = new PendingUserDeletionOptionsValidator()
            .Validate(null, new PendingUserDeletionOptions { RunInterval = TimeSpan.Parse(interval, System.Globalization.CultureInfo.InvariantCulture) });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("00:01:00")]
    [InlineData("02:00:00")]
    [InlineData("1.00:00:00")]
    public void Validator_AcceptsAnIntervalFromOneMinuteToOneDay(string interval)
    {
        var result = new PendingUserDeletionOptionsValidator()
            .Validate(null, new PendingUserDeletionOptions { RunInterval = TimeSpan.Parse(interval, System.Globalization.CultureInfo.InvariantCulture) });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Registration_BindsTheSection()
    {
        var options = BuildOptions("Production", new()
        {
            ["PendingUserDeletion:Enabled"] = "false",
            ["PendingUserDeletion:RunInterval"] = "00:30:00",
        });

        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(30), options.RunInterval);
    }

    /// <summary>
    /// The E2E database is recreated on every start and its users are fixtures; the job must not
    /// delete them, or call Auth0 for them, whatever the untracked appsettings file says.
    /// </summary>
    [Fact]
    public void Registration_SwitchesTheJobOff_InE2ETesting_EvenWhenConfigured()
    {
        var options = BuildOptions("E2ETesting", new() { ["PendingUserDeletion:Enabled"] = "true" });

        Assert.False(options.Enabled);
    }

    [Fact]
    public void Registration_AddsTheHostedJob()
    {
        var services = new ServiceCollection();
        services.AddPendingUserDeletion(new ConfigurationBuilder().Build(), new TestHostEnvironment("Production"));

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(PendingUserDeletionJob));
    }

    [Fact]
    public void TheTestHost_DoesNotRunTheJobOnItsOwn()
    {
        Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), s => s is PendingUserDeletionJob);
    }

    private static PendingUserDeletionOptions BuildOptions(string environment, Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddPendingUserDeletion(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new TestHostEnvironment(environment));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<PendingUserDeletionOptions>>().Value;
    }

    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "PrintLogApi";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    #endregion

    #region RunOnceAsync against the real service

    /// <summary>
    /// Moved from the anonymous endpoint's controller test when the endpoint was removed: a deleted
    /// user's prints take with them the notifications other users got about those prints. A user
    /// still inside the grace period is left alone.
    /// </summary>
    [Fact]
    public async Task RunOnce_DeletesAnExpiredUser_AndNotificationsAboutTheirPrints_ButNotAUserInsideTheGracePeriod()
    {
        Guid notificationId;
        long userId;
        long printId;
        long recentlyDeactivatedId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var now = DateTime.UtcNow;
            var deactivatedUser = new User
            {
                OAuthUserId = $"auth0|pending-delete-{Guid.NewGuid()}",
                DeactivationDateTime = DateTimeOffset.UtcNow.AddDays(-2),
                ViewStatus = ProfileViewStatus.Public
            };
            var recentlyDeactivated = new User
            {
                OAuthUserId = $"auth0|recently-deactivated-{Guid.NewGuid()}",
                DeactivationDateTime = DateTimeOffset.UtcNow.AddHours(-1),
                ViewStatus = ProfileViewStatus.Public
            };
            var recipientUser = new User
            {
                OAuthUserId = $"auth0|notification-recipient-{Guid.NewGuid()}",
                ViewStatus = ProfileViewStatus.Public
            };
            db.Users.AddRange(deactivatedUser, recentlyDeactivated, recipientUser);
            await db.SaveChangesAsync(Ct);

            var printer = new Printer
            {
                Name = "Pending Delete Printer",
                UserId = deactivatedUser.Id,
                IsActive = true
            };
            db.Printers.Add(printer);
            await db.SaveChangesAsync(Ct);

            var print = new Print
            {
                Title = "Pending Delete Print",
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Public,
                PrinterId = printer.Id,
                CreatedById = deactivatedUser.Id,
                UpdatedById = deactivatedUser.Id,
                CreatedDate = now,
                UpdatedDate = now
            };
            db.Prints.Add(print);
            await db.SaveChangesAsync(Ct);

            var notification = new Notification
            {
                Id = Guid.NewGuid(),
                UserId = recipientUser.Id,
                Type = NotificationType.PrintCompleted,
                Title = "Print Completed",
                Message = "A followed print completed.",
                IsRead = false,
                CreatedDate = now,
                PrintId = print.Id
            };
            db.Notifications.Add(notification);
            await db.SaveChangesAsync(Ct);

            userId = deactivatedUser.Id;
            printId = print.Id;
            notificationId = notification.Id;
            recentlyDeactivatedId = recentlyDeactivated.Id;
        }

        var job = new PendingUserDeletionJob(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new PendingUserDeletionOptions()),
            TimeProvider.System,
            NullLogger<PendingUserDeletionJob>.Instance);

        var result = await job.RunOnceAsync(Ct);

        Assert.Equal(0, result.Failed);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            Assert.False(await db.Users.AnyAsync(u => u.Id == userId, Ct));
            Assert.False(await db.Prints.AnyAsync(p => p.Id == printId, Ct));
            Assert.False(await db.Notifications.AnyAsync(n => n.Id == notificationId, Ct));
            Assert.True(await db.Users.AnyAsync(u => u.Id == recentlyDeactivatedId, Ct));
        }
    }

    #endregion

    #region The loop, on a hand-advanced clock

    [Fact]
    public async Task Disabled_NeverRuns_AndStartsNoTimer()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var deletion = new ScriptedDeletionService();
        using var job = CreateJob(deletion, clock, enabled: false);

        await job.StartAsync(Ct);

        // ExecuteAsync runs on its own task (.NET 10), so StartAsync can return before it does.
        Assert.NotNull(job.ExecuteTask);
        await job.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(job.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(0, clock.TimerCount);
        clock.Advance(PendingUserDeletionJob.InitialDelay + Interval);
        Assert.Equal(0, deletion.Calls);
        await job.StopAsync(Ct);
    }

    [Fact]
    public async Task FirstRun_WaitsForTheInitialDelay()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var deletion = new ScriptedDeletionService();
        using var job = CreateJob(deletion, clock);

        await job.StartAsync(Ct);
        await clock.WaitForTimersAsync(1);
        clock.Advance(PendingUserDeletionJob.InitialDelay - TimeSpan.FromSeconds(1));
        Assert.Equal(0, deletion.Calls);

        clock.Advance(TimeSpan.FromSeconds(1));
        await deletion.WaitForCallsAsync(1);

        await job.StopAsync(Ct);
    }

    [Fact]
    public async Task AThrowingRun_IsLogged_AndTheNextTickRunsAgain()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var deletion = new ScriptedDeletionService();
        deletion.Next(_ => throw new InvalidOperationException("Simulated database outage."));
        var logger = new CapturingLogger();
        using var job = CreateJob(deletion, clock, logger: logger);

        await job.StartAsync(Ct);
        await clock.WaitForTimersAsync(1);
        clock.Advance(PendingUserDeletionJob.InitialDelay);
        await deletion.WaitForCallsAsync(1);

        clock.Advance(Interval);
        await deletion.WaitForCallsAsync(2);

        Assert.Contains(logger.Errors, e => e is InvalidOperationException);
        await job.StopAsync(Ct);
        Assert.True(job.ExecuteTask!.IsCompletedSuccessfully);
    }

    /// <summary>
    /// The timer is awaited between runs, so a tick that comes due while a run is still going is
    /// held until it ends, and never starts a second run beside it.
    /// </summary>
    [Fact]
    public async Task ALongRun_IsNeverOverlappedByTheNextTick()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var deletion = new ScriptedDeletionService();
        var release = new TaskCompletionSource();
        deletion.Next(async _ =>
        {
            await release.Task;
            return new PendingUserDeletionResult(0, 0, 0);
        });
        using var job = CreateJob(deletion, clock);

        await job.StartAsync(Ct);
        await clock.WaitForTimersAsync(1);
        clock.Advance(PendingUserDeletionJob.InitialDelay);
        await deletion.WaitForCallsAsync(1);

        clock.Advance(Interval);
        clock.Advance(Interval);
        Assert.Equal(1, deletion.Calls);

        release.SetResult();
        await deletion.WaitForCallsAsync(2);

        Assert.Equal(1, deletion.MaxConcurrent);
        await job.StopAsync(Ct);
    }

    [Fact]
    public async Task StoppingDuringTheInitialDelay_EndsCleanly()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var deletion = new ScriptedDeletionService();
        using var job = CreateJob(deletion, clock);

        await job.StartAsync(Ct);
        await clock.WaitForTimersAsync(1);
        await job.StopAsync(Ct);

        Assert.True(job.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(0, deletion.Calls);
    }

    private static PendingUserDeletionJob CreateJob(
        ScriptedDeletionService deletion, TimeProvider clock, bool enabled = true, ILogger<PendingUserDeletionJob>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IUserDeletionService>(_ => deletion);
        // Left undisposed: it holds nothing but the scripted service, which the test still reads.
        var provider = services.BuildServiceProvider();
        return new PendingUserDeletionJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new PendingUserDeletionOptions { Enabled = enabled, RunInterval = Interval }),
            clock,
            logger ?? NullLogger<PendingUserDeletionJob>.Instance);
    }

    /// <summary>An <see cref="IUserDeletionService"/> whose runs a test scripts, and counts.</summary>
    private sealed class ScriptedDeletionService : IUserDeletionService
    {
        private readonly Queue<Func<CancellationToken, Task<PendingUserDeletionResult>>> _script = new();
        private readonly SemaphoreSlim _called = new(0);
        private int _calls;
        private int _running;
        private int _maxConcurrent;
        private int _waited;

        public int Calls => Volatile.Read(ref _calls);

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public void Next(Func<CancellationToken, Task<PendingUserDeletionResult>> run) => _script.Enqueue(run);

        public Task DeleteAllDataForUser(User user) => throw new NotSupportedException();

        public async Task<PendingUserDeletionResult> DeletePendingDeactivatedUsers(CancellationToken ct = default)
        {
            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref _maxConcurrent, running);
            Interlocked.Increment(ref _calls);
            _called.Release();
            try
            {
                Func<CancellationToken, Task<PendingUserDeletionResult>>? run;
                lock (_script)
                {
                    _script.TryDequeue(out run);
                }

                return run is null ? new PendingUserDeletionResult(0, 0, 0) : await run(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public async Task WaitForCallsAsync(int count)
        {
            while (_waited < count)
            {
                Assert.True(await _called.WaitAsync(TimeSpan.FromSeconds(10), Ct), $"The job did not run a {count}. time.");
                _waited++;
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value
                && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    private sealed class CapturingLogger : ILogger<PendingUserDeletionJob>
    {
        private readonly List<Exception> _errors = new();

        public IReadOnlyList<Exception> Errors
        {
            get
            {
                lock (_errors)
                {
                    return _errors.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error && exception != null)
            {
                lock (_errors)
                {
                    _errors.Add(exception);
                }
            }
        }
    }

    /// <summary>
    /// A clock whose timers fire only when a test advances it. Covers what the job uses:
    /// <c>Task.Delay</c> and <c>PeriodicTimer</c> over a <see cref="TimeProvider"/>, both of which
    /// create one timer and drive it through <see cref="ITimer.Change"/>.
    /// </summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = new();
        private DateTimeOffset _now = start;

        public int TimerCount
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Count;
                }
            }
        }

        /// <summary>Waits until <paramref name="count"/> timers exist: the job creates them on its own task.</summary>
        public async Task WaitForTimersAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (TimerCount < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"The job did not create {count} timer(s).");
                await Task.Delay(TimeSpan.FromMilliseconds(5), Ct);
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += by;
                due = _timers.Where(t => t.DueAt <= _now).ToList();
                foreach (var timer in due)
                {
                    timer.DueAt = timer.Period == Timeout.InfiniteTimeSpan ? null : _now + timer.Period;
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? DueAt { get; set; }

            public TimeSpan Period { get; private set; } = Timeout.InfiniteTimeSpan;

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                    Period = period;
                }

                return true;
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    #endregion
}
