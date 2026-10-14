using Prometheus;

namespace PrintLogApi.Services.Spoolman;

/// <summary>
/// Prometheus series for the Spoolman-compatible API, scraped with the existing <c>/metrics</c>.
/// Never labelled by user or connection: that would be unbounded cardinality. Per-user usage
/// goes to Application Insights instead (see <c>SpoolmanController.Use</c>).
/// </summary>
public static class SpoolmanMetrics
{
    /// <summary><c>PUT /spool/{id}/use</c> calls accepted (#177), by the unit they reported in.</summary>
    public static readonly Counter UseCalls = Metrics.CreateCounter(
        "printlog_spoolman_use_calls_total", "Spoolman /use reports accepted.", "unit");

    /// <summary>
    /// What became of each accepted report (#171), labelled by <see cref="OutcomeLabel"/>. A high
    /// <c>no_print</c> share means printers using Spoolman without the notifier.
    /// </summary>
    public static readonly Counter UseRecorded = Metrics.CreateCounter(
        "printlog_spoolman_use_recorded_total", "Spoolman /use reports by what became of them.", "outcome");

    /// <summary>Millimetres of filament those calls reported, weights converted with the spool's density and diameter.</summary>
    public static readonly Counter UseMillimetres = Metrics.CreateCounter(
        "printlog_spoolman_use_mm_total", "Millimetres of filament reported by Spoolman /use calls.");

    /// <summary>The <c>outcome</c> label: the outcome in lower snake case.</summary>
    public static string OutcomeLabel(SpoolmanUseOutcome outcome) => outcome switch
    {
        SpoolmanUseOutcome.Recorded => "recorded",
        SpoolmanUseOutcome.NoPrinter => "no_printer",
        SpoolmanUseOutcome.Bridge => "bridge",
        SpoolmanUseOutcome.NoPrint => "no_print",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };
}
