using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Profiles;

namespace PrintLogApi.Services.Analytics;

/// <summary>
/// The Materials tab. Everything here groups over PrintFilament rows joined to their spool,
/// which is the ONLY grain at which a type, brand or colour exists.
///
/// Consequence, deliberate and tested: the group totals fall short of /overview's filament
/// total by exactly the "other filament" scalar (Print.FilamentUsageMg), which records
/// material never attached to a tracked spool and therefore has no attributes to group by.
/// It is reported in coverage rather than silently absorbed into a bucket it does not
/// belong to.
///
/// <para>Filament adjustments count too (#231), as their own signed "adjustment" figures beside
/// print consumption: in each series bucket, each group and each top spool. A spool's remaining
/// weight already subtracts them, so a burn rate without them would divide one population by
/// another. Coverage still counts prints only; the response's AdjustmentCount and
/// RowCapExceeded speak for adjustments.</para>
///
/// <para>A spool filter treats the two differently, on purpose. It keeps prints that used one
/// of the spools, and those prints bring every usage row they hold, other spools' included, so
/// a multi-material print still shows its other materials. An adjustment belongs to one spool
/// and nothing else, so only the named spools' adjustments are counted. Printer, project and
/// status filters leave adjustments out entirely (<see cref="AdjustmentsApply"/>).</para>
/// </summary>
public sealed class MaterialAnalyticsService(PrintLogContext context, TimeProvider timeProvider)
    : IMaterialAnalyticsService
{
    public const int MaxGroups = 15;
    public const int MaxSpools = 25;
    public const int BurnRateWindowDays = 90;
    public const int MaxRunwayDays = 365;

    /// <summary>The synthetic rollup key, shared by the group lists and the time series.</summary>
    public const string OtherKey = "__other";

    /// <summary>Neutral grey, used when a spool records no colour at all (spec §10).</summary>
    public const string UnknownSwatchColor = "#9e9e9e";

    /// <summary>
    /// The four numbers this tab needs about one filtered print set. Named rather than
    /// anonymous so <see cref="StatsQuery"/> can be lifted out of <c>Compute</c> and its
    /// translation asserted directly.
    /// </summary>
    public sealed record MaterialScopeStats(int Total, int Undated, int UsageRows, int Unattributed)
    {
        public static readonly MaterialScopeStats Empty = new(0, 0, 0, 0);
    }

    /// <summary>
    /// Four numbers about one filtered set, in one aggregate rather than four scans. This does
    /// not use <see cref="AnalyticsPrintCounts"/> because two of the four are specific to this
    /// tab: the usage-ROW cap and the unattributed-material print count.
    ///
    /// Exposed separately from its execution so its translation can be asserted against the
    /// production provider without a database — see the note on
    /// <see cref="AnalyticsPrintCounts.Query"/>.
    /// </summary>
    public static IQueryable<MaterialScopeStats> StatsQuery(IQueryable<Print> scoped) =>
        scoped
            .GroupBy(_ => 1)
            .Select(g => new MaterialScopeStats(
                g.Count(),
                g.Count(p => p.StartDate == null),
                // Bound the second stage on the grain it actually materializes. Every widget
                // on this tab is built from PrintFilament ROWS, not prints, so a print-count
                // cap would let a multi-material library stream several times its own limit
                // into memory (spec §6.4).
                g.Sum(p => p.FilamentUsage!.Count()),
                // Material that was never attached to a spool has no attributes to group by.
                // Count the PRINTS carrying it, so the coverage note reads as "N prints used
                // filament that is not linked to a spool" — without this the tab silently
                // disagrees with Overview.
                g.Count(p =>
                    (p.FilamentUsageMg.HasValue && p.FilamentUsageMg > 0)
                    || (!(p.FilamentUsageMg.HasValue && p.FilamentUsageMg > 0)
                        && p.EstimatedFilamentUsageMg.HasValue && p.EstimatedFilamentUsageMg > 0))));

    /// <summary>What a usage row and an adjustment row both know about their spool: enough to group and draw it.</summary>
    private interface ISpoolRow
    {
        Guid FilamentId { get; }
        string? Brand { get; }
        string? MaterialType { get; }
        string? ColorName { get; }
        IReadOnlyList<string> Colors { get; }
        int ColorPattern { get; }
        int FinishType { get; }
        IReadOnlyList<int> Effects { get; }
    }

    private sealed record UsageRow(
        long PrintId,
        Guid FilamentId, string? DisplayName, string? Brand, string? MaterialType, string? ColorName,
        IReadOnlyList<string> Colors, int ColorPattern, int FinishType, IReadOnlyList<int> Effects,
        DateTimeOffset? StartDate, Print.PrintStatus Status, long UsedMg) : ISpoolRow;

    /// <summary>
    /// One adjustment as consumption (#231): <see cref="ConsumedMg"/> is the negated signed amount,
    /// so filament removed is positive and filament added back negative.
    /// </summary>
    private sealed record AdjustmentRow(
        Guid FilamentId, string? Brand, string? MaterialType, string? ColorName,
        IReadOnlyList<string> Colors, int ColorPattern, int FinishType, IReadOnlyList<int> Effects,
        DateTimeOffset Date, long ConsumedMg) : ISpoolRow;

    /// <summary>The columns <see cref="AdjustmentsQuery"/> reads, before the swatch and sign are resolved in memory.</summary>
    public sealed record AdjustmentQueryRow(
        Guid FilamentId, string? Brand, string? MaterialType, string? ColorName,
        List<string>? Colors, string? ColorHex, Enums.ColorPatternType? ColorPattern,
        Enums.FilamentFinishType? FinishType, List<Enums.FilamentEffect>? Effects,
        DateTime Date, double AmountMg);

    /// <summary>
    /// Nothing in this product predates it. A timestamp before it is a default value, not a date:
    /// #256 leaves adjustments with CreatedDate = 0001-01-01 when a filament-detail save re-sends
    /// them without one.
    /// </summary>
    public static readonly DateTime EarliestPlausibleDate = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// When an adjustment happened, for filtering and bucketing alike: its CreatedDate, else its
    /// UpdatedDate, else its spool's CreatedDate.
    ///
    /// <para>A CreatedDate lost to #256 cannot be recovered. The save that cleared it also stamped
    /// UpdatedDate, which is the latest the adjustment can have been made, so it is the best date
    /// left. If that is no better, the spool's creation is the earliest it can have been made, and
    /// is still a real date. Either way the row keeps counting, as it does in the spool's remaining
    /// weight; dropping it would make the tab disagree with the spool page.</para>
    ///
    /// <para>One expression, used by the window filter and the projection, so a row is never
    /// counted in a window but bucketed outside it.</para>
    /// </summary>
    public static readonly Expression<Func<FilamentAdjustment, DateTime>> AdjustmentDate = a =>
        a.CreatedDate >= EarliestPlausibleDate ? a.CreatedDate
        : a.UpdatedDate >= EarliestPlausibleDate ? a.UpdatedDate
        : a.Filament.CreatedDate;

    /// <summary>
    /// Whether adjustments can belong to this filter at all. An adjustment has a spool and a
    /// timestamp, but no printer, project or status, so a filter on any of those describes prints
    /// it cannot be part of, and the tab shows prints alone.
    /// </summary>
    public static bool AdjustmentsApply(AnalyticsFilter filter) =>
        filter.PrinterIds.Count == 0 && filter.ProjectIds.Count == 0 && filter.Statuses.Count == 0;

    /// <summary>
    /// The user's adjustments in the filter's window (#231), on their own spools, restricted to the
    /// filter's spools when it names any. Half-open [from, to) on <see cref="AdjustmentDate"/>, a
    /// UTC <see cref="DateTime"/>. The amount is <see cref="FilamentProfile.AdjustmentAmountMg"/>, the
    /// same lambda the spool's remaining weight sums. Call only when <see cref="AdjustmentsApply"/>.
    ///
    /// <para>Exposed so its translation can be asserted against SQL Server without a database, like
    /// <see cref="StatsQuery"/>.</para>
    /// </summary>
    public static IQueryable<AdjustmentQueryRow> AdjustmentsQuery(
        IQueryable<FilamentAdjustment> adjustments, long userId, AnalyticsFilter filter)
    {
        var scoped = adjustments.Where(a => a.Filament.CreatedById == userId);

        if (filter.HasRange)
        {
            var from = filter.FromDate.Value.UtcDateTime;
            var to = filter.ToDate.Value.UtcDateTime;
            scoped = scoped.Where(ExpressionSplice.With<FilamentAdjustment, DateTime, bool>(
                (a, date) => date >= from && date < to, AdjustmentDate));
        }

        // Only the named spools' adjustments. Prints work differently: the filter keeps prints
        // that used one of the spools, and every usage row on those prints comes along, other
        // spools' included. An adjustment belongs to exactly one spool, so there is no "print"
        // to bring company with it.
        if (filter.FilamentIds.Count > 0)
            scoped = scoped.Where(a => filter.FilamentIds.Contains(a.FilamentId));

        return scoped
            .OrderBy(AdjustmentDate)
            .SelectWith((a, amount, date) => new AdjustmentQueryRow(
                a.FilamentId,
                a.Filament.Brand,
                a.Filament.MaterialType,
                a.Filament.ColorName,
                a.Filament.Colors,
                a.Filament.ColorHex,
                a.Filament.ColorPattern,
                a.Filament.FinishType,
                a.Filament.Effects,
                date(a),
                amount(a)), FilamentProfile.AdjustmentAmountMg, AdjustmentDate);
    }

    public async Task<MaterialsResponse> GetMaterials(long userId, AnalyticsFilter filter, CancellationToken ct)
    {
        // ONE clock read per request, taken here at the entry point and threaded down. A
        // request can compute two windows (current and previous), and each of those closes an
        // open end at "now" — reading the clock per computation would let a single response
        // measure its two halves against two different instants.
        var now = timeProvider.GetUtcNow();

        var current = await Compute(userId, filter, now, includeAdjustments: true, ct);

        var previousFilter = PreviousWindow.For(filter, now);
        if (previousFilter is null) return current;

        // Only its waste figures are read, and those are prints alone.
        var previous = await Compute(userId, previousFilter, now, includeAdjustments: false, ct);

        // The two waste figures are this tab's scalar tiles. Burn rate and runway are
        // deliberately excluded: both already describe a trailing window, so a delta against
        // another trailing window would compare two overlapping periods.
        return current with
        {
            WasteGrams = current.WasteGrams with
            {
                Previous = PreviousWindow.Usable(previous.WasteGrams.Value),
            },
            WasteCost = current.WasteCost with
            {
                Previous = PreviousWindow.Usable(previous.WasteCost.Value),
            },
        };
    }

    /// <param name="now">
    /// The caller's single clock read. A filter with no ToDate means "up to now", so this is
    /// what closes the window — it is a parameter rather than a field read so that the two
    /// computations behind one response cannot disagree about when "now" was.
    /// </param>
    /// <param name="includeAdjustments">
    /// False for the previous window, whose waste figures are all that is read from it.
    /// </param>
    private async Task<MaterialsResponse> Compute(
        long userId, AnalyticsFilter filter, DateTimeOffset now, bool includeAdjustments, CancellationToken ct)
    {
        // `now` is the caller's single clock read (see the entry point above). The burn-rate
        // window Runway measures is anchored to the same instant the series ends at, so a
        // second read here could give a spool a runway computed against a window the chart
        // beside it does not show.

        filter.TryResolveTimeZone(out var zone);
        zone ??= TimeZoneInfo.Utc;
        var granularity = filter.ResolveGranularity();

        var scoped = AnalyticsQueryScope.Scope(
            context.Prints.AsNoTracking(), userId, filter, filter.FromDate, filter.ToDate);

        var stats = await StatsQuery(scoped).FirstOrDefaultAsync(ct) ?? MaterialScopeStats.Empty;

        var coverage = new CoverageBuilder("prints")
        {
            Total = stats.Total,
        };
        coverage.UndatedCount = filter.HasRange ? 0 : stats.Undated;

        // Loaded once here and threaded through: Waste's projection needs the same settings,
        // and reading them twice is both an extra round-trip and a chance for one response to
        // price two things off two different reads of the same rate.
        var costInputs = await AnalyticsCostProjection.LoadInputs(context, userId, ct);

        if (stats.UsageRows > AnalyticsService.MaxSeriesRows)
        {
            coverage.Exclude(ExclusionReason.RowCapExceeded, coverage.Total);
            return EmptyMaterials(filter, granularity, costInputs, coverage.Build());
        }

        // Adjustment rows share the row budget with usage rows: both are materialized here.
        var adjustments = Array.Empty<AdjustmentRow>() as IReadOnlyList<AdjustmentRow>;
        if (includeAdjustments && AdjustmentsApply(filter))
        {
            var budget = AnalyticsService.MaxSeriesRows - stats.UsageRows;
            var loaded = await AdjustmentsQuery(context.FilamentAdjustments.AsNoTracking(), userId, filter)
                .Take(budget + 1)
                .ToListAsync(ct);

            if (loaded.Count > budget)
            {
                coverage.Exclude(ExclusionReason.RowCapExceeded, coverage.Total);
                return EmptyMaterials(filter, granularity, costInputs, coverage.Build());
            }

            adjustments = loaded
                .Select(a => new AdjustmentRow(
                    a.FilamentId, a.Brand, a.MaterialType, a.ColorName,
                    SwatchColors(a.Colors, a.ColorHex),
                    (int)(a.ColorPattern ?? Enums.ColorPatternType.Solid),
                    (int)(a.FinishType ?? Enums.FilamentFinishType.Standard),
                    (a.Effects ?? new List<Enums.FilamentEffect>()).Select(e => (int)e).ToList(),
                    new DateTimeOffset(DateTime.SpecifyKind(a.Date, DateTimeKind.Utc)),
                    -(long)Math.Round(a.AmountMg)))
                // An adjustment of nothing says nothing, like a usage row with no amount below.
                .Where(a => a.ConsumedMg != 0)
                .ToList();
        }

        coverage.Exclude(ExclusionReason.UnattributedMaterial, stats.Unattributed);

        // Flattened with the result selector and filtered OUTSIDE the SelectMany, not with a
        // Where inside it. The inner-filter form is a correlated subquery, which needs SQL
        // APPLY — unsupported on SQLite, so it throws under the integration-test provider
        // while working on SQL Server. This form is a plain INNER JOIN on every provider.
        var rows = (await scoped
            .SelectMany(p => p.FilamentUsage!, (p, pf) => new { p, pf })
            .Where(x => x.pf.FilamentId.HasValue
                && x.pf.Filament != null
                && x.pf.Filament.CreatedById == userId)
            .SelectWith((x, row) => new
            {
                PrintId = x.p.Id,
                // Guarded by the Where above; same expression tree as the x.pf.Filament! on the
                // next line, so the same rule applies - ! only, or the translation changes.
                FilamentId = x.pf.FilamentId!.Value,
                x.pf.Filament!.DisplayName,
                x.pf.Filament.Brand,
                x.pf.Filament.MaterialType,
                x.pf.Filament.ColorName,
                x.pf.Filament.Colors,
                x.pf.Filament.ColorHex,
                x.pf.Filament.ColorPattern,
                x.pf.Filament.FinishType,
                x.pf.Filament.Effects,
                x.p.StartDate,
                x.p.Status,
                // Usage folded into milligrams, whatever measure the row was recorded in, by
                // FilamentProfile.UsageRowMg itself, so analytics and the spool pages resolve a
                // row identically. PrintService normally fills all three measures in; a row
                // written around it carries only what the user entered.
                UsedMg = (long)Math.Round(row(x.pf)),
            }, FilamentProfile.UsageRowMg)
            .ToListAsync(ct))
            .Select(r => new UsageRow(
                r.PrintId,
                r.FilamentId, r.DisplayName, r.Brand, r.MaterialType, r.ColorName,
                SwatchColors(r.Colors, r.ColorHex),
                (int)(r.ColorPattern ?? Enums.ColorPatternType.Solid),
                (int)(r.FinishType ?? Enums.FilamentFinishType.Standard),
                (r.Effects ?? new List<Enums.FilamentEffect>()).Select(e => (int)e).ToList(),
                r.StartDate, r.Status, r.UsedMg))
            .ToList();

        // A usage row with no recorded amount resolves to 0 and is dropped here rather than
        // carried into grouping: it would otherwise produce a zero-mass material group and a
        // zero-use "top spool", and make the tab report itself non-empty on the strength of
        // rows that say nothing. The ROW CAP above deliberately still counts them, because
        // its job is bounding what gets materialized, and these rows are materialized.
        rows = rows.Where(r => r.UsedMg > 0).ToList();

        // Counted and Total must describe the SAME population, which the builder names as
        // "prints". Distinct filaments is a different population, and reporting it here
        // produced impossible coverage — "20 of 5" for a library with more spools than
        // prints in range.
        coverage.Counted = rows.Select(r => r.PrintId).Distinct().Count();

        var byType = Group(rows, adjustments, r => Label(r.MaterialType));
        var byBrand = Group(rows, adjustments, r => Label(r.Brand));
        var byColor = Group(rows, adjustments, r => Label(r.ColorName));

        // The series must use the SAME key set the groups were truncated to. Keyed on raw
        // material type it would emit buckets under keys the UI has no series for — the UI
        // builds its series from ByType — and every type ranked below the cap would vanish
        // from a stacked chart the user reads as a total.
        var series = BuildSeries(
            rows, adjustments, byType, filter, zone, granularity, now,
            await SeriesFloor(userId, filter, rows, adjustments, ct));
        var spools = await TopSpools(userId, rows, adjustments, costInputs, ct);
        var runway = Runway(spools, rows, adjustments, filter, now);
        var (wasteGrams, wasteCost, currency) = await Waste(userId, scoped, costInputs, coverage, ct);

        return new MaterialsResponse(
            filter.FromDate, filter.ToDate, filter.TimeZone, granularity.ToString(), currency,
            byType, byBrand, byColor, series, spools, runway,
            wasteGrams, wasteCost, coverage.Build(), adjustments.Count, false);
    }

    /// <summary>
    /// The colour tokens for a swatch: the spool's own list, else its legacy single hex.
    ///
    /// Blank entries are dropped rather than passed through, and a spool with NO recorded
    /// colour gets neutral grey (spec §10) instead of a token the client would resolve to
    /// black — "we do not know this spool's colour" and "this spool is black" are different
    /// claims, and the swatch must not make the second one on the first one's evidence.
    /// </summary>
    private static IReadOnlyList<string> SwatchColors(List<string>? colors, string? colorHex)
    {
        var tokens = (colors ?? new List<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .ToList();

        if (tokens.Count > 0) return tokens;
        if (!string.IsNullOrWhiteSpace(colorHex)) return new List<string> { colorHex.Trim() };
        return new List<string> { UnknownSwatchColor };
    }

    /// <summary>
    /// The key a row groups under. Blank and whitespace-only metadata is "Unknown", not its
    /// own nameless group, and surrounding whitespace is trimmed so " PLA " and "PLA" are
    /// one material rather than two. Case is deliberately preserved: folding it would change
    /// the label the user chose for their own spool.
    /// </summary>
    private static string Label(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();

    /// <summary>
    /// Top MaxGroups by mass plus an aggregated "Other". Truncating without the rollup would
    /// silently lose mass from a chart the user reads as a total.
    ///
    /// <para>A group exists for every key with print usage, or with adjustments that net to
    /// filament removed (#231), so a spool only ever weighed in still shows where its filament
    /// went. A key with no prints whose adjustments net to zero or to filament added back has no
    /// mass, and is dropped for the reason a usage row with no amount is: a zero-mass group, or
    /// a negative slice of "Other". A group with prints stays whatever its adjustments say. Mass
    /// is prints plus adjustments, which is what the ranking uses; the two stay separate fields
    /// so the chart can tell print activity from inventory corrections.</para>
    /// </summary>
    private static IReadOnlyList<MaterialGroup> Group(
        IReadOnlyList<UsageRow> rows, IReadOnlyList<AdjustmentRow> adjustments, Func<ISpoolRow, string> key)
    {
        var usageByKey = rows.ToLookup(r => key(r));
        var adjustmentsByKey = adjustments.ToLookup(a => key(a));

        var groups = usageByKey.Select(g => g.Key)
            .Union(adjustmentsByKey.Select(g => g.Key))
            .Select(k =>
            {
                var usage = usageByKey[k].ToList();
                var adjusted = adjustmentsByKey[k].ToList();

                // The spool that contributed most to the group draws its swatch: by print use
                // where there is any, otherwise by the adjustment that removed the most.
                ISpoolRow exemplar = usage.Count > 0
                    ? usage.OrderByDescending(r => r.UsedMg).First()
                    : adjusted.OrderByDescending(a => a.ConsumedMg).First();

                return new MaterialGroup(
                    k, k,
                    // DISTINCT prints. Counting usage rows would rank a material used twice
                    // on one multi-material print above a material used on two prints, which
                    // is not what "most used" means (spec §5).
                    usage.Select(r => r.PrintId).Distinct().Count(),
                    usage.Sum(r => r.UsedMg),
                    new SwatchDto(exemplar.Colors, exemplar.ColorPattern, exemplar.FinishType, exemplar.Effects),
                    adjusted.Sum(a => a.ConsumedMg));
            })
            .Where(g => g.PrintCount > 0 || g.MaterialMg + g.AdjustmentMg > 0)
            .OrderByDescending(g => g.MaterialMg + g.AdjustmentMg).ThenBy(g => g.Key)
            .ToList();

        if (groups.Count <= MaxGroups) return groups;

        var kept = groups.Take(MaxGroups).ToList();
        var rest = groups.Skip(MaxGroups).ToList();
        kept.Add(new MaterialGroup(
            OtherKey, "Other",
            // Summed, not de-duplicated: one print can appear under several rolled-up
            // groups, so this is "group appearances", and the label makes no per-print claim.
            rest.Sum(g => g.PrintCount),
            rest.Sum(g => g.MaterialMg),
            new SwatchDto(new List<string> { UnknownSwatchColor },
                (int)Enums.ColorPatternType.Solid, (int)Enums.FilamentFinishType.Standard, new List<int>()),
            rest.Sum(g => g.AdjustmentMg)));
        return kept;
    }

    /// <summary>
    /// The earliest an all-time series may start (#256): the earliest of the account's creation,
    /// the first print and the creation of the oldest adjusted spool, never before
    /// <see cref="EarliestPlausibleDate"/>. Only consulted for an all-time request with
    /// adjustments, so the extra reads are skipped everywhere else.
    ///
    /// <para><see cref="AdjustmentDate"/> already dates every adjustment by something real; this
    /// is the backstop that keeps a stray timestamp from asking the bucketer for thousands of
    /// buckets, or for a local midnight before <see cref="DateTime.MinValue"/>, which throws east
    /// of UTC. The adjusted spools' creation is a candidate because no adjustment can predate
    /// its spool, so a genuine adjustment is never pulled forward; the account's creation alone
    /// would not do, since older accounts do not record one.</para>
    /// </summary>
    private async Task<DateTimeOffset> SeriesFloor(
        long userId, AnalyticsFilter filter, IReadOnlyList<UsageRow> rows,
        IReadOnlyList<AdjustmentRow> adjustments, CancellationToken ct)
    {
        var plausible = new DateTimeOffset(EarliestPlausibleDate);
        if (filter.HasRange || adjustments.Count == 0) return plausible;

        var accountCreated = await context.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.CreatedDate)
            .FirstOrDefaultAsync(ct);
        var firstPrint = rows.Where(r => r.StartDate.HasValue).Select(r => (DateTimeOffset?)r.StartDate!.Value).Min();
        var spoolIds = adjustments.Select(a => a.FilamentId).Distinct().ToList();
        var firstSpool = await context.Filaments.AsNoTracking()
            .Where(f => f.CreatedById == userId && spoolIds.Contains(f.Id))
            .Select(f => (DateTime?)f.CreatedDate)
            .MinAsync(ct);

        var candidates = new[]
            {
                accountCreated,
                firstPrint,
                firstSpool is { } spool ? new DateTimeOffset(DateTime.SpecifyKind(spool, DateTimeKind.Utc)) : null,
            }
            .OfType<DateTimeOffset>()
            .ToList();
        if (candidates.Count == 0) return plausible;

        var floor = candidates.Min();
        return floor > plausible ? floor : plausible;
    }

    /// <summary>
    /// Grams per bucket, split by material type — using the SAME keys ByType was truncated
    /// to. A type that was rolled into "Other" for the group chart is rolled into "Other"
    /// here too, so the stacked series conserves mass instead of dropping everything ranked
    /// below the cap on the floor.
    ///
    /// <para>Adjustments (#231) are one separate, signed series per bucket, by CreatedDate,
    /// rather than folded into the per-type figures, so the chart keeps print activity and
    /// inventory corrections apart. An all-time series starts at the earliest print OR
    /// adjustment, but never before <paramref name="floor"/>: an adjustment dated earlier is
    /// counted in the first bucket rather than stretching the chart back to it.</para>
    /// </summary>
    private static IReadOnlyList<MaterialSeriesBucket> BuildSeries(
        IReadOnlyList<UsageRow> rows, IReadOnlyList<AdjustmentRow> adjustments,
        IReadOnlyList<MaterialGroup> byType,
        AnalyticsFilter filter, TimeZoneInfo zone, AnalyticsGranularity granularity,
        DateTimeOffset now, DateTimeOffset floor)
    {
        var retained = byType.Select(g => g.Key).ToHashSet();
        var dated = rows.Where(r => r.StartDate.HasValue).ToList();
        if (dated.Count == 0 && adjustments.Count == 0) return Array.Empty<MaterialSeriesBucket>();

        var earliest = dated.Select(r => r.StartDate!.Value).Concat(adjustments.Select(a => a.Date)).Min();
        var from = filter.FromDate ?? (earliest > floor ? earliest : floor);
        var to = filter.ToDate ?? now;
        if (to <= from) return Array.Empty<MaterialSeriesBucket>();

        var buckets = TimeBucketer.BuildBuckets(from, to, zone, granularity, DayOfWeek.Sunday);
        var accumulator = buckets.ToDictionary(b => b.Index, _ => new Dictionary<string, long>());
        var adjusted = buckets.ToDictionary(b => b.Index, _ => 0L);

        foreach (var row in dated)
        {
            var index = TimeBucketer.IndexOf(buckets, row.StartDate!.Value.ToUniversalTime());
            if (index < 0) continue;

            var label = Label(row.MaterialType);
            var key = retained.Contains(label) ? label : OtherKey;
            var slot = accumulator[buckets[index].Index];
            slot[key] = (slot.TryGetValue(key, out var n) ? n : 0) + row.UsedMg;
        }

        foreach (var adjustment in adjustments)
        {
            // Only an all-time series can hold an adjustment before its start; a ranged one
            // filtered it out in SQL.
            var index = TimeBucketer.IndexOf(buckets, adjustment.Date < from ? from : adjustment.Date);
            if (index < 0) continue;

            adjusted[buckets[index].Index] += adjustment.ConsumedMg;
        }

        return buckets
            .Select(b => new MaterialSeriesBucket(
                b.Index, b.LocalStart, (IReadOnlyDictionary<string, long>)accumulator[b.Index],
                adjusted[b.Index]))
            .ToList();
    }

    /// <summary>
    /// Remaining is <see cref="FilamentProfile.RemainingMg"/>, spliced into the projection so
    /// there is no second copy of the rule to drift. Negative values are reported as-is.
    ///
    /// <para>Adjustments (#231): a spool is listed when the window holds print usage on it or
    /// an adjustment that removed filament from it, and spools rank by prints plus adjustments.
    /// PercentConsumed and CostConsumed describe that same net figure, floored at zero, since
    /// a window where more was added back than used consumed nothing.</para>
    /// </summary>
    private async Task<IReadOnlyList<SpoolRow>> TopSpools(
        long userId, IReadOnlyList<UsageRow> rows, IReadOnlyList<AdjustmentRow> adjustments,
        CostInputs inputs, CancellationToken ct)
    {
        var usedByFilament = rows
            .GroupBy(r => r.FilamentId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.UsedMg));
        var adjustedByFilament = adjustments
            .GroupBy(a => a.FilamentId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.ConsumedMg));

        var ids = usedByFilament.Keys
            .Union(adjustedByFilament.Where(kv => kv.Value > 0).Select(kv => kv.Key))
            .ToList();

        if (ids.Count == 0) return Array.Empty<SpoolRow>();

        var spools = await context.Filaments.AsNoTracking()
            .Where(f => f.CreatedById == userId && ids.Contains(f.Id))
            .Select(ExpressionSplice.With((Filament f, long? remainingMg) => new
            {
                f.Id,
                f.DisplayName,
                f.Brand,
                f.MaterialType,
                f.ColorName,
                f.InitialNominalWeightMg,
                // Priced through PrintCostCalculator below, never with ad-hoc arithmetic:
                // the currency gate and the default-price fallback live there.
                f.PurchasePriceValue,
                f.PurchasePriceCurrency,
                f.MaterialDensityGramPerCubicCm,
                f.DiameterMm,
                RemainingMg = remainingMg,
            }, FilamentProfile.RemainingMg))
            .ToListAsync(ct);

        var swatches = rows.Cast<ISpoolRow>().Concat(adjustments)
            .GroupBy(r => r.FilamentId)
            .ToDictionary(
                g => g.Key,
                g => new SwatchDto(g.First().Colors, g.First().ColorPattern, g.First().FinishType, g.First().Effects));

        return spools
            .Select(s =>
            {
                var used = usedByFilament.GetValueOrDefault(s.Id);
                var adjusted = adjustedByFilament.GetValueOrDefault(s.Id);
                var consumed = Math.Max(0, used + adjusted);

                return new SpoolRow(
                    s.Id.ToString(),
                    string.Join(" · ", new[] { s.DisplayName, s.Brand, s.MaterialType, s.ColorName }
                        .Where(x => !string.IsNullOrWhiteSpace(x))),
                    swatches[s.Id],
                    used,
                    s.RemainingMg,
                    s.InitialNominalWeightMg,
                    s.InitialNominalWeightMg is > 0
                        ? 100.0 * consumed / s.InitialNominalWeightMg.Value
                        : null,
                    CostConsumed(s.PurchasePriceValue, s.PurchasePriceCurrency, s.InitialNominalWeightMg,
                        s.MaterialDensityGramPerCubicCm, s.DiameterMm, consumed, inputs),
                    adjusted);
            })
            .OrderByDescending(s => s.UsedMg + s.AdjustmentMg).ThenBy(s => s.FilamentId)
            .Take(MaxSpools)
            .ToList();
    }

    /// <summary>
    /// Cost of the material actually consumed off one spool, priced by weight because
    /// <paramref name="consumedMg"/> is already resolved milligrams. Routing it through
    /// PrintCostCalculator rather than multiplying inline is what keeps the currency-mismatch and
    /// default-price rules identical to every other cost figure in the product.
    ///
    /// <para>The calculator treats zero grams as "no usable amount" and returns null, which
    /// would read as "cannot be priced". A spool that consumed nothing but can be priced costs
    /// 0, so zero is priced as a gram to find out whether pricing works at all.</para>
    /// </summary>
    private static decimal? CostConsumed(
        string? priceValue, string? priceCurrency, long? initialMg, double density, double? diameterMm,
        long consumedMg, CostInputs inputs)
    {
        decimal? Price(long mg) => PrintCostCalculator.FilamentCost(
            new[]
            {
                new FilamentCostRow(
                    priceValue, priceCurrency, initialMg, density, diameterMm,
                    (int)Filament.SourceMeasurement.Weight, mg, null, null,
                    (int)Filament.SourceMeasurement.Weight, null, null, null),
            },
            inputs).Amount;

        if (consumedMg > 0) return Price(consumedMg);
        return Price(1000) is null ? null : 0m;
    }

    /// <summary>
    /// Burn rate over the trailing min(window, 90 days), NOT the whole filter range: a
    /// five-year range would compute a rate from an era that says nothing about this month.
    /// Runway is suppressed at zero burn and never extrapolated past a year.
    ///
    /// <para>Burn is prints plus adjustments (#231), because the remaining weight it divides
    /// already counts both. The burn window is the period whose net consumption is floored at
    /// zero: a window where more filament was added back than used burned nothing, rather than
    /// burning a negative amount and producing a negative runway.</para>
    /// </summary>
    private static IReadOnlyList<RunwayRow> Runway(
        IReadOnlyList<SpoolRow> spools, IReadOnlyList<UsageRow> rows, IReadOnlyList<AdjustmentRow> adjustments,
        AnalyticsFilter filter, DateTimeOffset now)
    {
        var windowTo = filter.ToDate ?? now;
        var windowFrom = filter.FromDate ?? windowTo.AddDays(-BurnRateWindowDays);
        var days = Math.Min((windowTo - windowFrom).TotalDays, BurnRateWindowDays);
        if (days <= 0) return Array.Empty<RunwayRow>();

        var burnFrom = windowTo.AddDays(-days);
        var printed = rows
            .Where(r => r.StartDate.HasValue && r.StartDate.Value >= burnFrom && r.StartDate.Value < windowTo)
            .Select(r => (r.FilamentId, Mg: r.UsedMg));
        var adjusted = adjustments
            .Where(a => a.Date >= burnFrom && a.Date < windowTo)
            .Select(a => (a.FilamentId, Mg: a.ConsumedMg));
        var recent = printed.Concat(adjusted)
            .GroupBy(x => x.FilamentId)
            .ToDictionary(g => g.Key, g => Math.Max(0, g.Sum(x => x.Mg)) / 1000.0 / days);

        return spools
            .Where(s => s.RemainingMg.HasValue)
            .Select(s =>
            {
                var id = Guid.Parse(s.FilamentId);
                var burn = recent.TryGetValue(id, out var rate) ? rate : 0;
                var remainingGrams = s.RemainingMg!.Value / 1000.0;

                return new RunwayRow(
                    s.FilamentId, s.Label, s.Swatch, remainingGrams, burn,
                    burn <= 0 || remainingGrams <= 0
                        ? null
                        : Math.Min(MaxRunwayDays, remainingGrams / burn));
            })
            .OrderBy(r => r.RunwayDays ?? double.MaxValue).ThenBy(r => r.FilamentId)
            .ToList();
    }

    /// <summary>
    /// Failed + Cancelled only. PartialSuccess is deliberately excluded: partial output has
    /// value, and folding it in would inflate "waste" with prints the user actually used.
    /// </summary>
    private async Task<(Metric Grams, MoneyMetric Cost, string Currency)> Waste(
        long userId, IQueryable<Print> scoped, CostInputs inputs,
        CoverageBuilder coverage, CancellationToken ct)
    {
        var wasted = scoped.Where(p =>
            p.Status == Print.PrintStatus.Failed || p.Status == Print.PrintStatus.Cancelled);

        var wastedMg = await wasted.SumAsync(PrintMetrics.MaterialMgExpr, ct);
        var wastedCount = await wasted.CountAsync(ct);

        // The COST metric carries its own coverage. The stat tile renders the honesty note
        // from the metric it is given, not from the tab, so pricing exclusions recorded only
        // at tab level would leave "Cost of waste" showing a partial — or null — figure with
        // nothing on the tile to say why.
        var costCoverage = new CoverageBuilder("prints") { Total = wastedCount };

        var projection = await AnalyticsCostProjection.Project(context, userId, wasted, ct, inputs);
        decimal? cost = null;
        if (projection.RowCapExceeded)
        {
            coverage.Exclude(ExclusionReason.RowCapExceeded, projection.PrintCount);
            costCoverage.Exclude(ExclusionReason.RowCapExceeded, projection.PrintCount);
        }
        else
        {
            var priced = projection.Prints.Where(p => p.Total.HasValue).ToList();
            cost = priced.Count > 0 ? priced.Sum(p => p.Total ?? 0m) : null;
            costCoverage.Counted = priced.Count;

            foreach (var (reason, count) in AnalyticsCostProjection.CountExclusions(projection.Prints))
            {
                coverage.Exclude(reason, count);
                costCoverage.Exclude(reason, count);
            }
        }

        // Mass does not depend on pricing, so the grams metric keeps a clean record: every
        // wasted print's material is counted, whether or not it could be costed.
        var massCoverage = new CoverageBuilder("prints") { Counted = wastedCount, Total = wastedCount }.Build();

        return (
            new Metric(wastedMg / 1000.0, null, massCoverage),
            new MoneyMetric(cost, null, projection.Inputs.UserCurrency, costCoverage.Build()),
            projection.Inputs.UserCurrency);
    }

    /// <summary>
    /// The shape returned when the row cap bites. Everything is empty, and RowCapExceeded and the
    /// coverage record say why (the coverage alone cannot when the window holds no prints) — an empty tab with no explanation reads as "you have no materials".
    /// </summary>
    private static MaterialsResponse EmptyMaterials(
        AnalyticsFilter filter, AnalyticsGranularity granularity, CostInputs inputs, Coverage coverage) =>
        new(filter.FromDate, filter.ToDate, filter.TimeZone, granularity.ToString(),
            inputs.UserCurrency,
            Array.Empty<MaterialGroup>(), Array.Empty<MaterialGroup>(), Array.Empty<MaterialGroup>(),
            Array.Empty<MaterialSeriesBucket>(), Array.Empty<SpoolRow>(), Array.Empty<RunwayRow>(),
            new Metric(null, null, coverage),
            new MoneyMetric(null, null, inputs.UserCurrency, coverage),
            coverage, 0, true);
}
