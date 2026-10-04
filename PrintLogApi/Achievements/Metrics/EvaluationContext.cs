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

    public long UserId { get; }

    public PrintLogContext Db { get; }

    /// <summary>The user's time zone for date metrics; UTC when unset or unresolvable.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>The pass's single clock reading. Metrics never read the clock themselves.</summary>
    public DateTime NowUtc { get; }

    /// <summary>Every metric measured so far in this pass, by key.</summary>
    public IReadOnlyDictionary<string, MetricValue> Measured => _measured;

    public async Task<PrintAggregate> GetPrintAggregateAsync(CancellationToken ct) =>
        _aggregate ??= await PrintAggregate.LoadAsync(Db, UserId, ct);

    public async Task<IReadOnlyList<PrintDateRow>> GetPrintDatesAsync(CancellationToken ct) =>
        _dates ??= await Db.Prints
            .AsNoTracking()
            .Where(p => p.CreatedById == UserId)
            .Select(p => new PrintDateRow(p.StartDate, p.CreatedDate, p.Status))
            .ToListAsync(ct);

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
