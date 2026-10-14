using Prometheus;

namespace PrintLogApi.Services.Spoolman;

/// <summary>
/// Open Spoolman sockets (#178), labelled by tier and nothing else: a user or connection label
/// would be unbounded cardinality. B1 allows 350 sockets per instance; plan the move to S1 or
/// P0v3 at about 250 (see the Usage tab of the monitoring workbook).
/// </summary>
public static class SpoolmanSocketMetrics
{
    public const string Free = "free";
    public const string Pro = "pro";

    public static readonly Gauge OpenSockets = Metrics.CreateGauge(
        "printlog_spoolman_open_sockets", "Open Spoolman websockets.", "tier");
}
