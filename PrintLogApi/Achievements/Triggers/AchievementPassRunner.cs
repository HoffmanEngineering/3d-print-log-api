using System.Globalization;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;

namespace PrintLogApi.Achievements.Triggers;

public interface IAchievementPassRunner
{
    /// <summary>Runs one triggered evaluation pass per affected user. Never throws.</summary>
    Task RunAsync(PendingWork work, CancellationToken ct);
}

/// <summary>
/// Runs passes in fresh DI scopes. Every context it creates is suppressed before anything
/// resolves from it, so the grants a pass saves can never trigger another pass.
/// </summary>
public sealed class AchievementPassRunner(
    IServiceScopeFactory scopes,
    AchievementTriggerTracker tracker,
    TelemetryClient telemetry,
    ILogger<AchievementPassRunner> logger) : IAchievementPassRunner
{
    public async Task RunAsync(PendingWork work, CancellationToken ct)
    {
        Dictionary<long, AchievementTrigger> byUser;
        try
        {
            byUser = await ResolveOwnersAsync(work, ct);
        }
        catch (Exception ex)
        {
            Fail(ex, userId: null);
            return;
        }

        foreach (var (userId, flags) in byUser)
        {
            try
            {
                using var scope = scopes.CreateScope();
                tracker.Suppress(scope.ServiceProvider.GetRequiredService<PrintLogContext>());
                // Resolved after the suppression, from the same scope: the evaluator receives
                // exactly the context that was just suppressed.
                var evaluator = scope.ServiceProvider.GetRequiredService<IAchievementEvaluator>();
                await evaluator.EvaluateAsync(userId, flags, EvaluationMode.Triggered, ct);
            }
            catch (Exception ex)
            {
                Fail(ex, userId);
            }
        }
    }

    /// <summary>Folds the print-keyed triggers into their prints' owners, in one query.</summary>
    private async Task<Dictionary<long, AchievementTrigger>> ResolveOwnersAsync(PendingWork work, CancellationToken ct)
    {
        var byUser = new Dictionary<long, AchievementTrigger>(work.ByUser);
        if (work.PrintIdTriggers.Count == 0)
        {
            return byUser;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        tracker.Suppress(db);
        var printIds = work.PrintIdTriggers.Keys.ToList();
        var owners = await db.Prints
            .AsNoTracking()
            .Where(p => printIds.Contains(p.Id))
            .Select(p => new { p.Id, p.CreatedById })
            .ToListAsync(ct);

        foreach (var owner in owners)
        {
            byUser[owner.CreatedById] = byUser.GetValueOrDefault(owner.CreatedById) | work.PrintIdTriggers[owner.Id];
        }
        return byUser;
    }

    private void Fail(Exception ex, long? userId)
    {
        logger.LogError(ex, "Achievement pass failed for user {UserId}; reconciliation will catch up", userId);
        telemetry.TrackEvent("AchievementPass_Failed", new Dictionary<string, string>
        {
            ["UserId"] = userId?.ToString(CultureInfo.InvariantCulture) ?? "",
            ["Exception"] = ex.GetType().Name,
        });
    }
}
