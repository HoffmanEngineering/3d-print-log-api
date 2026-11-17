using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PrintLogApi.Achievements.Triggers;

/// <summary>
/// Records achievement triggers on every async save and runs them once the data is committed:
/// immediately when the save was its own transaction, or from
/// <see cref="AchievementTransactionInterceptor"/> when an explicit transaction commits.
/// <para>
/// Synchronous saves raise nothing. Only the test-data seeders save synchronously, and anything
/// they miss is granted by reconciliation the next time the user loads their achievements.
/// </para>
/// <para>
/// Never fails the save that triggered it: extraction and the pass are both contained here, and
/// the runner contains its own failures.
/// </para>
/// </summary>
public sealed class AchievementSaveChangesInterceptor(
    AchievementTriggerTracker tracker,
    IAchievementPassRunner runner,
    AchievementPassOptions options,
    ILogger<AchievementSaveChangesInterceptor> logger) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } ctx && !tracker.IsSuppressed(ctx))
        {
            try
            {
                tracker.RecordTentative(ctx, TriggerExtractor.Extract(ctx));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to record achievement triggers; reconciliation will catch up");
            }
        }

        return ValueTask.FromResult(result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not { } ctx || tracker.IsSuppressed(ctx))
        {
            return result;
        }

        var transaction = ctx.Database.CurrentTransaction;
        tracker.PromoteTentative(ctx, transaction?.TransactionId);
        if (transaction is null)
        {
            await AchievementPasses.RunAsync(runner, tracker.TakePending(ctx, null), options, logger);
        }

        return result;
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } ctx) tracker.DiscardTentative(ctx);
        return Task.CompletedTask;
    }

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } ctx) tracker.DiscardTentative(ctx);
        return Task.CompletedTask;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } ctx) tracker.DiscardTentative(ctx);
    }

    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        if (eventData.Context is { } ctx) tracker.DiscardTentative(ctx);
    }
}

/// <summary>Hands work to the runner without ever letting a failure reach the caller's save.</summary>
internal static class AchievementPasses
{
    public static async Task RunAsync(IAchievementPassRunner runner, PendingWork work, AchievementPassOptions options, ILogger logger)
    {
        if (work.IsEmpty)
        {
            return;
        }

        // Not the request's token: a client that disconnects after its save committed still
        // earned the badge. Bounded instead, so a stuck pass can't hold the request (or the
        // connection an explicit transaction still owns) for long.
        using var budget = new CancellationTokenSource(options.Budget);
        try
        {
            await runner.RunAsync(work, budget.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Achievement pass failed; reconciliation will catch up");
        }
    }
}
