using Prometheus;

namespace PrintLogApi.Services.Spoolman;

/// <summary>
/// Prometheus series for the Spoolman-compatible API, scraped with the existing <c>/metrics</c>.
/// Never labelled by user or connection: that would be unbounded cardinality. Per-user usage
/// goes to Application Insights instead (see <c>SpoolmanController.Use</c>).
/// </summary>
public static class SpoolmanMetrics
{
    /// <summary><c>PUT /spool/{id}/use</c> calls accepted (#177), to size recording them (#171).</summary>
    public static readonly Counter UseCalls = Metrics.CreateCounter(
        "printlog_spoolman_use_calls_total", "Spoolman /use reports accepted (not persisted).", "unit");

    /// <summary>Millimetres of filament those calls reported, weights converted with the spool's density and diameter.</summary>
    public static readonly Counter UseMillimetres = Metrics.CreateCounter(
        "printlog_spoolman_use_mm_total", "Millimetres of filament reported by Spoolman /use calls.");
}
