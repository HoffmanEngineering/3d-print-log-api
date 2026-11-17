using System.Collections.Concurrent;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;

namespace PrintLogApi.IntegrationTests.Telemetry;

/// <summary>
/// Captures everything the app would have sent to Application Insights, so a test can assert
/// on the events an endpoint emits. Replaces the SDK's server channel in
/// <see cref="CustomWebApplicationFactory"/>.
/// </summary>
/// <remarks>
/// Items arrive here only after the whole pipeline has run — initializers, then processors,
/// then sampling — so what a test sees is what production would have shipped, including the
/// <c>authMethod</c> and user stamps and minus anything the noise processor dropped.
/// </remarks>
public sealed class RecordingTelemetryChannel : ITelemetryChannel
{
    private readonly ConcurrentQueue<ITelemetry> _items = new();

    public bool? DeveloperMode { get; set; }
    public string? EndpointAddress { get; set; }

    public IReadOnlyCollection<ITelemetry> Items => _items.ToArray();

    public IEnumerable<EventTelemetry> Events(string name)
        => _items.OfType<EventTelemetry>().Where(e => e.Name == name);

    public void Send(ITelemetry item) => _items.Enqueue(item);

    public void Clear() => _items.Clear();

    public void Flush()
    {
    }

    public void Dispose()
    {
    }
}
