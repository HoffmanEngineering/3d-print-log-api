using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using PrintLogApi.Models;

namespace PrintLogApi.Achievements.Triggers;

/// <summary>
/// Reads a context's pending changes into achievement triggers. Never queries the database.
/// <para>
/// "Changed" always means the original value differs from the current one, never
/// <c>IsModified</c>: <c>PrintService.UpdatePrint</c> forces the whole print to
/// <c>EntityState.Modified</c>, which flags every property, while the tracked entity's original
/// values still hold what was loaded. Filament usage is compared as a per-print multiset for the
/// same reason: an edit replaces the rows wholesale, so a delete-and-re-add of identical values is
/// no change at all.
/// </para>
/// </summary>
public static class TriggerExtractor
{
    private static readonly string[] PrintMetricProperties =
    [
        nameof(Print.Status), nameof(Print.StartDate), nameof(Print.PrintTimeInSeconds),
        nameof(Print.EstimatedPrintTimeInSeconds), nameof(Print.ViewStatus), nameof(Print.ProjectId),
        // The "other filament" scalar is part of the canonical material total.
        nameof(Print.FilamentUsageMg), nameof(Print.EstimatedFilamentUsageMg),
    ];

    private static readonly string[] ProfileProperties =
        [nameof(User.DisplayName), nameof(User.ProfilePicture), nameof(User.Bio)];

    private readonly record struct UsageSignature(Guid? FilamentId, int? AmountMg, int? EstimatedAmountMg);

    public static PendingWork Extract(DbContext ctx)
    {
        var byUser = new Dictionary<long, AchievementTrigger>();
        var byPrint = new Dictionary<long, AchievementTrigger>();
        var usageBefore = new Dictionary<long, List<UsageSignature>>();
        var usageAfter = new Dictionary<long, List<UsageSignature>>();

        void ForUser(long userId, AchievementTrigger flag) => byUser[userId] = byUser.GetValueOrDefault(userId) | flag;
        void ForPrint(long printId, AchievementTrigger flag) => byPrint[printId] = byPrint.GetValueOrDefault(printId) | flag;

        foreach (var entry in ctx.ChangeTracker.Entries())
        {
            var state = entry.State;
            switch (entry.Entity)
            {
                case Print print:
                    if (state == EntityState.Added) ForUser(print.CreatedById, AchievementTrigger.PrintAdded);
                    else if (state == EntityState.Modified && Changed(entry, PrintMetricProperties)) ForUser(print.CreatedById, AchievementTrigger.PrintUpdated);
                    break;

                case PrintFilament usage when !IsNewPrint(entry):
                    // "Before" is keyed by the print the row belonged to when loaded. An edit
                    // swaps in fresh instances, which arrive Modified with an original PrintId of
                    // 0: those never existed before, so they count only on the "after" side.
                    var originalPrintId = (long)entry.Property(nameof(PrintFilament.PrintId)).OriginalValue!;
                    if (state is EntityState.Deleted or EntityState.Modified && originalPrintId > 0)
                        Bucket(usageBefore, originalPrintId).Add(Signature(entry, original: true));
                    if (state is EntityState.Added or EntityState.Modified && usage.PrintId > 0)
                        Bucket(usageAfter, usage.PrintId).Add(Signature(entry, original: false));
                    break;

                case Filament filament:
                    if (state is EntityState.Added or EntityState.Deleted
                        || (state == EntityState.Modified && Changed(entry, nameof(Filament.MaterialType))))
                        ForUser(filament.CreatedById, AchievementTrigger.FilamentChanged);
                    break;

                case Printer printer when state is EntityState.Added or EntityState.Deleted:
                    ForUser(printer.UserId, AchievementTrigger.PrinterChanged);
                    break;

                case Project project when state is EntityState.Added or EntityState.Deleted:
                    ForUser(project.CreatedById, AchievementTrigger.ProjectChanged);
                    break;

                // Only a printer's owner can log maintenance on it, so the creator is the owner.
                case PrinterMaintenance maintenance when state is EntityState.Added or EntityState.Deleted:
                    ForUser(maintenance.CreatedById, AchievementTrigger.MaintenanceChanged);
                    break;

                case PrintImage image when state == EntityState.Added:
                    ForUser(image.CreatedById, AchievementTrigger.PrintImageAdded);
                    break;

                // Credited to the print's owner, not the commenter; the runner resolves the owner.
                case PrintComment comment when state == EntityState.Added && comment.PrintId > 0:
                    ForPrint(comment.PrintId, AchievementTrigger.CommentReceived);
                    break;

                case User user when state == EntityState.Modified && Changed(entry, ProfileProperties):
                    ForUser(user.Id, AchievementTrigger.ProfileChanged);
                    break;

                case UserSetting { UserSettingTypeId: Metrics.EvaluationContext.TimeZoneSettingTypeId, UserId: { } ownerId }
                    when state == EntityState.Added || (state == EntityState.Modified && Changed(entry, nameof(UserSetting.Value))):
                    ForUser(ownerId, AchievementTrigger.TimeZoneChanged);
                    break;
            }
        }

        foreach (var printId in usageBefore.Keys.Union(usageAfter.Keys))
        {
            var before = usageBefore.GetValueOrDefault(printId) ?? [];
            var after = usageAfter.GetValueOrDefault(printId) ?? [];
            if (!SameMultiset(before, after)) ForPrint(printId, AchievementTrigger.PrintUpdated);
        }

        return byUser.Count == 0 && byPrint.Count == 0 ? PendingWork.Empty : new PendingWork(byUser, byPrint);
    }

    private static bool Changed(EntityEntry entry, params string[] properties) =>
        properties.Any(p =>
        {
            var property = entry.Property(p);
            return !Equals(property.OriginalValue, property.CurrentValue);
        });

    /// <summary>Usage on a print created in the same save is already covered by PrintAdded.</summary>
    private static bool IsNewPrint(EntityEntry usage) =>
        usage.Reference(nameof(PrintFilament.Print)).TargetEntry?.State == EntityState.Added;

    private static UsageSignature Signature(EntityEntry entry, bool original)
    {
        object? Value(string name) => original ? entry.Property(name).OriginalValue : entry.Property(name).CurrentValue;
        return new UsageSignature(
            (Guid?)Value(nameof(PrintFilament.FilamentId)),
            (int?)Value(nameof(PrintFilament.AmountMg)),
            (int?)Value(nameof(PrintFilament.EstimatedAmountMg)));
    }

    private static List<UsageSignature> Bucket(Dictionary<long, List<UsageSignature>> map, long printId)
    {
        if (!map.TryGetValue(printId, out var list))
        {
            map[printId] = list = [];
        }
        return list;
    }

    private static bool SameMultiset(List<UsageSignature> a, List<UsageSignature> b) =>
        a.Count == b.Count && Sorted(a).SequenceEqual(Sorted(b));

    private static IEnumerable<UsageSignature> Sorted(List<UsageSignature> list) =>
        list.OrderBy(s => s.FilamentId).ThenBy(s => s.AmountMg).ThenBy(s => s.EstimatedAmountMg);
}
