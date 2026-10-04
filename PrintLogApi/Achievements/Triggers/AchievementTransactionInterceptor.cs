using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PrintLogApi.Achievements.Triggers;

/// <summary>
/// Runs the triggers recorded inside an explicit transaction once it commits, and drops them if
/// it rolls back or fails. A transaction an execution strategy abandons for a retry never commits,
/// so only the attempt that does commit runs. EF's own per-save transactions also pass through
/// here, but their triggers are not promoted until the save completes, so they find nothing.
/// </summary>
public sealed class AchievementTransactionInterceptor(
    AchievementTriggerTracker tracker,
    IAchievementPassRunner runner,
    ILogger<AchievementTransactionInterceptor> logger) : DbTransactionInterceptor
{
    public override async Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } ctx && !tracker.IsSuppressed(ctx))
        {
            await AchievementPasses.RunAsync(runner, tracker.TakePending(ctx, eventData.TransactionId), logger);
        }
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        // Synchronous commits raise nothing, matching synchronous saves.
        if (eventData.Context is { } ctx) tracker.DiscardPending(ctx, eventData.TransactionId);
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        TransactionRolledBack(transaction, eventData);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (eventData.Context is { } ctx) tracker.DiscardPending(ctx, eventData.TransactionId);
    }

    public override Task TransactionFailedAsync(
        DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        TransactionFailed(transaction, eventData);
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        if (eventData.Context is { } ctx) tracker.DiscardPending(ctx, eventData.TransactionId);
    }
}
