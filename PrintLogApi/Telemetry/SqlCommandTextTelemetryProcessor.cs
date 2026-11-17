using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace PrintLogApi.Telemetry;

/// <summary>
/// Keeps a SQL dependency's command text only where someone will read it: calls that failed or
/// took <see cref="SlowThreshold"/> or longer. Every other SQL row is still sent, without it.
/// </summary>
/// <remarks>
/// <para>
/// Startup turns on <c>EnableSqlCommandTextInstrumentation</c> so a DTU spike can be traced to
/// a statement. There are about 1.1M SQL dependencies a month, and EF's statements run to
/// kilobytes, so keeping the text on all of them would push ingestion past the free allowance.
/// Slow and failed calls are the few a dashboard is opened for.
/// </para>
/// <para>
/// Privacy: the text is the statement EF sends, which is parameterized (<c>@__userId_0</c>), and
/// the SDK does not record parameter values. The raw SQL in the code (the catalog applocks, the
/// spool-number reservation) carries no user values in its text either.
/// </para>
/// </remarks>
public sealed class SqlCommandTextTelemetryProcessor(ITelemetryProcessor next) : ITelemetryProcessor
{
    public static readonly TimeSpan SlowThreshold = TimeSpan.FromMilliseconds(500);

    public void Process(ITelemetry item)
    {
        if (item is DependencyTelemetry { Type: "SQL" } dependency
            && dependency.Success != false
            && dependency.Duration < SlowThreshold)
        {
            dependency.Data = null;
        }

        next.Process(item);
    }
}
