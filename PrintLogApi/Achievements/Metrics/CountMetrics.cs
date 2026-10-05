using Microsoft.EntityFrameworkCore;

namespace PrintLogApi.Achievements.Metrics;

/// <summary>
/// The count, sum and max metrics. Print-based ones read the memoized
/// <see cref="PrintAggregate"/>; every other entity costs one count query, issued only when a
/// selected definition needs it. Values are in display units (hours, kg, counts).
/// </summary>
public static class CountMetrics
{
    private const long SecondsPerHour = 3600;
    private const long MgPerKg = 1_000_000;

    public static IEnumerable<IAchievementMetric> All()
    {
        yield return FromAggregate("prints.count", a => a.PrintCount);
        yield return FromAggregate("prints.hours", a => a.SuccessSeconds / SecondsPerHour);
        yield return FromAggregate("prints.materialKg", a => a.SuccessMaterialMg / MgPerKg);
        yield return FromAggregate("prints.longestHours", a => a.LongestSuccessSeconds / SecondsPerHour);
        yield return FromAggregate("prints.withImage", a => a.WithImage);
        yield return FromAggregate("prints.public", a => a.Public);
        yield return FromAggregate("prints.failed", a => a.Failed);
        yield return FromAggregate("prints.slicerPlugin", a => a.SlicerPlugin);
        yield return FromAggregate("source.octoprint", a => a.OctoPrint);
        yield return FromAggregate("source.moonraker", a => a.Moonraker);
        yield return FromAggregate("source.mcp", a => a.Mcp);
        foreach (var slicer in SlicerNames.BadgeSlicers)
        {
            yield return FromAggregate($"slicer.{slicer}", a => a.PluginSlicers.Contains(slicer) ? 1 : 0);
        }
        yield return FromAggregate("slicer.distinct", a => a.PluginSlicers.Count(s => s != SlicerNames.Other));
        yield return FromAggregate("prints.maxDistinctMaterials", a => a.MaxDistinctMaterials);
        yield return FromAggregate("prints.materialTypes", a => a.DistinctMaterialTypes);

        yield return FromQuery("printers.count", (ctx, ct) =>
            ctx.Db.Printers.LongCountAsync(p => p.UserId == ctx.UserId, ct));
        yield return FromQuery("filaments.count", (ctx, ct) =>
            ctx.Db.Filaments.LongCountAsync(f => f.CreatedById == ctx.UserId, ct));
        yield return FromQuery("projects.count", (ctx, ct) =>
            ctx.Db.Projects.LongCountAsync(p => p.CreatedById == ctx.UserId, ct));
        // Owned through the printer: only a printer's owner can log maintenance on it.
        yield return FromQuery("maintenance.count", (ctx, ct) =>
            ctx.Db.PrinterMaintenance.LongCountAsync(m => m.Printer.UserId == ctx.UserId, ct));
        yield return FromQuery("comments.distinctCommenters", (ctx, ct) =>
            ctx.Db.PrintComments
                .Where(c => c.Print.CreatedById == ctx.UserId && c.CreatedById != ctx.UserId)
                .Select(c => c.CreatedById)
                .Distinct()
                .LongCountAsync(ct));
        yield return FromQuery("profile.complete", async (ctx, ct) =>
            await ctx.Db.Users.AnyAsync(u => u.Id == ctx.UserId
                && u.DisplayName != null && u.DisplayName.Trim() != ""
                && u.ProfilePicture != null && u.ProfilePicture.Trim() != ""
                && u.Bio != null && u.Bio.Trim() != "", ct) ? 1 : 0);
    }

    private static DelegateMetric FromAggregate(string key, Func<PrintAggregate, long> select) =>
        new(key, async (ctx, ct) => MetricValue.Count(select(await ctx.GetPrintAggregateAsync(ct))));

    private static DelegateMetric FromQuery(string key, Func<EvaluationContext, CancellationToken, Task<long>> query) =>
        new(key, async (ctx, ct) => MetricValue.Count(await query(ctx, ct)));
}
