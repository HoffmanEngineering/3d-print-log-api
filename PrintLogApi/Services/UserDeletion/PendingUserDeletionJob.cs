using Microsoft.Extensions.Options;

namespace PrintLogApi.Services.UserDeletion;

/// <summary>
/// Deletes accounts whose deactivation grace period (<c>PendingUserDeactivationTimeInMinutes</c>)
/// is over, every <see cref="PendingUserDeletionOptions.RunInterval"/> (2 hours). It replaced the
/// <c>DeleteUsersPendingDeactivation</c> Azure Function, which called an anonymous
/// <c>DELETE /api/Users/pending-deactivation</c>: anyone could start this multi-table delete as
/// often as they liked. The work is <see cref="IUserDeletionService.DeletePendingDeactivatedUsers"/>,
/// which isolates each user, so one account's failure never stops the rest.
/// <para>
/// The first run waits five minutes so a cold start is not slowed. A failed run is logged and the
/// loop carries on; the next one picks up every user still due. Runs never overlap: the timer is
/// awaited between them, so a tick that comes due during a long run waits for it, and this job is
/// the only caller. The API is a single instance, so there is no cross-process lock either.
/// </para>
/// <para>
/// Like the other hosted services it runs only while the process is alive, so it needs the App
/// Service's Always On.
/// </para>
/// </summary>
public sealed class PendingUserDeletionJob(
    IServiceScopeFactory scopeFactory,
    IOptions<PendingUserDeletionOptions> options,
    TimeProvider timeProvider,
    ILogger<PendingUserDeletionJob> logger) : BackgroundService
{
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(options.Value.RunInterval, timeProvider);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Deleting users pending deactivation failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One pass, in its own scope: the deletion service and its DbContext are scoped.</summary>
    internal async Task<PendingUserDeletionResult> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var deletion = scope.ServiceProvider.GetRequiredService<IUserDeletionService>();
        return await deletion.DeletePendingDeactivatedUsers(ct);
    }
}
