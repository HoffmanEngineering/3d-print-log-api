using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace PrintLogApi.Achievements.Triggers;

/// <summary>
/// Per-context trigger state, correlated to that context's saves and transactions rather than
/// shared through an <c>AsyncLocal</c>. A save records tentative triggers; success promotes them
/// to pending, keyed by the open transaction (or none); a commit runs them, and a failed save, a
/// rollback or a failed transaction discards them. Contexts are weakly held, so state disappears
/// with its context — including the pending work of a transaction an execution strategy abandoned
/// mid-retry, which is never run.
/// </summary>
public sealed class AchievementTriggerTracker
{
    private sealed class State
    {
        public readonly object Gate = new();
        public bool Suppressed;
        public PendingWork? Tentative;
        public PendingWork Untransacted = PendingWork.Empty;
        public readonly Dictionary<Guid, PendingWork> ByTransaction = [];
    }

    private readonly ConditionalWeakTable<DbContext, State> _states = new();

    private State For(DbContext ctx) => _states.GetValue(ctx, _ => new State());

    /// <summary>
    /// Marks an evaluation pass's own context: its saves record nothing, so granting can never
    /// trigger another pass.
    /// </summary>
    public void Suppress(DbContext ctx)
    {
        var state = For(ctx);
        lock (state.Gate) state.Suppressed = true;
    }

    public bool IsSuppressed(DbContext ctx)
    {
        if (!_states.TryGetValue(ctx, out var state)) return false;
        lock (state.Gate) return state.Suppressed;
    }

    /// <summary>Records the triggers of a save attempt that has not completed yet.</summary>
    public void RecordTentative(DbContext ctx, PendingWork work)
    {
        var state = For(ctx);
        lock (state.Gate) state.Tentative = work;
    }

    /// <summary>The save succeeded: its triggers wait for <paramref name="transactionId"/> to commit, or for nothing.</summary>
    public void PromoteTentative(DbContext ctx, Guid? transactionId)
    {
        var state = For(ctx);
        lock (state.Gate)
        {
            if (state.Tentative is not { } work) return;
            state.Tentative = null;
            if (transactionId is { } tx)
            {
                state.ByTransaction[tx] = state.ByTransaction.GetValueOrDefault(tx, PendingWork.Empty).Merge(work);
            }
            else
            {
                state.Untransacted = state.Untransacted.Merge(work);
            }
        }
    }

    /// <summary>The save failed or was canceled: nothing it recorded may leak into a later save.</summary>
    public void DiscardTentative(DbContext ctx)
    {
        if (!_states.TryGetValue(ctx, out var state)) return;
        lock (state.Gate) state.Tentative = null;
    }

    /// <summary>Removes and returns the work waiting on <paramref name="transactionId"/> (null: on no transaction).</summary>
    public PendingWork TakePending(DbContext ctx, Guid? transactionId)
    {
        if (!_states.TryGetValue(ctx, out var state)) return PendingWork.Empty;
        lock (state.Gate)
        {
            if (transactionId is { } tx)
            {
                return state.ByTransaction.Remove(tx, out var work) ? work : PendingWork.Empty;
            }

            var untransacted = state.Untransacted;
            state.Untransacted = PendingWork.Empty;
            return untransacted;
        }
    }

    public void DiscardPending(DbContext ctx, Guid transactionId)
    {
        if (!_states.TryGetValue(ctx, out var state)) return;
        lock (state.Gate) state.ByTransaction.Remove(transactionId);
    }
}
