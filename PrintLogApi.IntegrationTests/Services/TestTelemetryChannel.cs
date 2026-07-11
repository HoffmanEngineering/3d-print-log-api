using System.Collections.Concurrent;
using Microsoft.ApplicationInsights.Channel;

namespace PrintLogApi.IntegrationTests.Services
{
    public class TestTelemetryChannel : ITelemetryChannel
    {
        public ConcurrentQueue<ITelemetry> Items { get; } = new();
        public bool? DeveloperMode { get; set; }
        public string EndpointAddress { get; set; }
        public void Send(ITelemetry item) => Items.Enqueue(item);
        public void Flush() { }
        public void Dispose() { }
    }
}
