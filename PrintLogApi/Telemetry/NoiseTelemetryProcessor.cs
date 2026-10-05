using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace PrintLogApi.Telemetry;

/// <summary>
/// Drops request telemetry that carries no information, and the SQL rows it spawns.
/// </summary>
/// <remarks>
/// <para>
/// Measured on 2026-09-19: CORS preflights and the notification unread-count poll were
/// ~1 GB of the API's 2.2 GB monthly ingestion — nearly half, for rows that only ever say
/// "the browser is still open". The Prometheus scrape is small but equally content-free.
/// Dropping them keeps the resource inside the free allowance and, just as importantly,
/// keeps request-rate charts from being a picture of polling cadence.
/// </para>
/// <para>
/// Only <em>successful</em> items are dropped. A failing preflight or poll is exactly the kind
/// of thing a dashboard exists to show. Exceptions and custom events are never touched.
/// </para>
/// <para>
/// A dropped poll's SQL dependency is matched through <c>Context.Operation.Name</c>, which the
/// SDK sets to the parent request's name on every child item. That is the only reliable link
/// available inside a processor: by the time the dependency is tracked, the request telemetry
/// has not yet been finalised, so nothing else ties the two together.
/// </para>
/// </remarks>
public sealed class NoiseTelemetryProcessor(ITelemetryProcessor next) : ITelemetryProcessor
{
    public const string UnreadCountRequestName = "GET Notifications/GetUnreadCount";
    public const string MetricsRequestName = "GET /metrics";

    public void Process(ITelemetry item)
    {
        if (ShouldDrop(item))
        {
            return;
        }

        next.Process(item);
    }

    private static bool ShouldDrop(ITelemetry item)
    {
        switch (item)
        {
            case RequestTelemetry request:
                return request.Success != false && IsNoiseRequest(request.Name);

            case DependencyTelemetry dependency:
                return dependency.Success != false
                    && dependency.Type == "SQL"
                    && dependency.Context.Operation.Name == UnreadCountRequestName;

            default:
                return false;
        }
    }

    private static bool IsNoiseRequest(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return name.StartsWith("OPTIONS ", StringComparison.Ordinal)
            || name == UnreadCountRequestName
            || name == MetricsRequestName;
    }
}
