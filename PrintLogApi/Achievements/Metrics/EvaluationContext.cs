using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;

namespace PrintLogApi.Achievements.Metrics;

/// <summary>One print's dates and status, as the date and streak metrics need them.</summary>
public sealed record PrintDateRow(DateTimeOffset? StartDate, DateTime CreatedDate, Print.PrintStatus Status);

/// <summary>
/// Per-pass state for measuring one user's metrics. Every shared query and every metric is
/// memoized here, so a pass costs a fixed number of queries whatever the user's history size:
/// one aggregate over prints, one date projection, and one query per other entity actually used.
/// Not thread-safe; a pass measures sequentially.
/// </summary>
public sealed class EvaluationContext
{
    private readonly Dictionary<string, IAchievementMetric> _metrics;
    private readonly Dictionary<string, MetricValue> _measured = new(StringComparer.Ordinal);
    private IReadOnlyList<PrintRow>? _rows;
    private PrintAggregate? _aggregate;
    private IReadOnlyList<PrintDateRow>? _dates;

    public EvaluationContext(long userId, PrintLogContext db, TimeZoneInfo zone, DateTime nowUtc, IEnumerable<IAchievementMetric> metrics)
    {
        UserId = userId;
        Db = db;
        Zone = zone;
        NowUtc = nowUtc;
        _metrics = metrics.ToDictionary(m => m.Key, StringComparer.Ordinal);
    }

    /// <summary>The <c>General_TimeZone</c> user setting type id.</summary>
    public const int TimeZoneSettingTypeId = 20;

    /// <summary>
    /// Creates a context for <paramref name="userId"/>, resolving their saved time zone once. A
    /// missing or unrecognized zone (an old client could have saved anything) means UTC, never an
    /// error: a bad setting must not block grants or saves.
    /// </summary>
    public static async Task<EvaluationContext> CreateAsync(
        PrintLogContext db, long userId, DateTime nowUtc, IEnumerable<IAchievementMetric> metrics, CancellationToken ct)
    {
        var zoneId = await db.UserSettings
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.UserSettingTypeId == TimeZoneSettingTypeId)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return new EvaluationContext(userId, db, Services.TimeZoneResolver.ResolveOrUtc(zoneId), nowUtc, metrics);
    }

    public long UserId { get; }

    public PrintLogContext Db { get; }

    /// <summary>The user's time zone for date metrics; UTC when unset or unresolvable.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>The pass's single clock reading. Metrics never read the clock themselves.</summary>
    public DateTime NowUtc { get; }

    /// <summary>Every metric measured so far in this pass, by key.</summary>
    public IReadOnlyDictionary<string, MetricValue> Measured => _measured;

    /// <summary>
    /// The user's prints, one narrow row each, read once and shared by the aggregate and every
    /// date metric. Served by the covering index on <c>Prints (CreatedById)</c>.
    /// </summary>
    public async Task<IReadOnlyList<PrintRow>> GetPrintRowsAsync(CancellationToken ct) =>
        _rows ??= await Db.Prints
            .AsNoTracking()
            .Where(p => p.CreatedById == UserId)
            .Select(p => new PrintRow(
                p.Id, p.StartDate, p.CreatedDate, p.Status, p.ViewStatus, p.Source, p.Slicer,
                p.PrintTimeInSeconds, p.EstimatedPrintTimeInSeconds, p.FilamentUsageMg, p.EstimatedFilamentUsageMg))
            .ToListAsync(ct);

    public async Task<PrintAggregate> GetPrintAggregateAsync(CancellationToken ct) =>
        _aggregate ??= await PrintAggregate.LoadAsync(Db, UserId, await GetPrintRowsAsync(ct), ct);

    public async Task<IReadOnlyList<PrintDateRow>> GetPrintDatesAsync(CancellationToken ct) =>
        _dates ??= (await GetPrintRowsAsync(ct)).Select(p => new PrintDateRow(p.StartDate, p.CreatedDate, p.Status)).ToList();

    /// <summary>Measures <paramref name="metricKey"/> once per pass.</summary>
    /// <exception cref="KeyNotFoundException">No metric is registered under that key.</exception>
    public async Task<MetricValue> MeasureAsync(string metricKey, CancellationToken ct)
    {
        if (_measured.TryGetValue(metricKey, out var cached))
        {
            return cached;
        }

        if (!_metrics.TryGetValue(metricKey, out var metric))
        {
            throw new KeyNotFoundException($"No achievement metric is registered as '{metricKey}'.");
        }

        var value = await metric.MeasureAsync(this, ct);
        _measured[metricKey] = value;
        return value;
    }
}
