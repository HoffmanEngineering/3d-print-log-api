using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using PrintLogApi.Telemetry;
using Xunit;

namespace PrintLogApi.IntegrationTests.Telemetry;

public class NoiseTelemetryProcessorTests
{
    private sealed class Sink : ITelemetryProcessor
    {
        public List<ITelemetry> Items { get; } = new();
        public void Process(ITelemetry item) => Items.Add(item);
    }

    private static (NoiseTelemetryProcessor processor, Sink sink) Build()
    {
        var sink = new Sink();
        return (new NoiseTelemetryProcessor(sink), sink);
    }

    private static RequestTelemetry Request(string name, bool? success = true)
        => new() { Name = name, Success = success };

    [Theory]
    [InlineData("OPTIONS /api/notifications/unread-count")]
    [InlineData("OPTIONS /api/Prints/summary")]
    [InlineData("GET Notifications/GetUnreadCount")]
    [InlineData("GET /metrics")]
    public void Drops_successful_noise_requests(string name)
    {
        var (processor, sink) = Build();

        processor.Process(Request(name));

        Assert.Empty(sink.Items);
    }

    [Theory]
    [InlineData("OPTIONS /api/Prints/summary")]
    [InlineData("GET Notifications/GetUnreadCount")]
    [InlineData("GET /metrics")]
    public void Keeps_failed_noise_requests(string name)
    {
        var (processor, sink) = Build();

        processor.Process(Request(name, success: false));

        Assert.Single(sink.Items);
    }

    [Theory]
    [InlineData("GET Prints/GetPrintSummary")]
    [InlineData("POST Prints/PostPrint")]
    [InlineData("GET /health")]
    [InlineData("GET /")]
    public void Keeps_ordinary_requests(string name)
    {
        var (processor, sink) = Build();

        processor.Process(Request(name));

        Assert.Single(sink.Items);
    }

    [Fact]
    public void Drops_successful_sql_dependency_of_the_unread_count_poll()
    {
        var (processor, sink) = Build();
        var dependency = new DependencyTelemetry { Type = "SQL", Success = true };
        dependency.Context.Operation.Name = NoiseTelemetryProcessor.UnreadCountRequestName;

        processor.Process(dependency);

        Assert.Empty(sink.Items);
    }

    [Fact]
    public void Keeps_failed_sql_dependency_of_the_unread_count_poll()
    {
        var (processor, sink) = Build();
        var dependency = new DependencyTelemetry { Type = "SQL", Success = false };
        dependency.Context.Operation.Name = NoiseTelemetryProcessor.UnreadCountRequestName;

        processor.Process(dependency);

        Assert.Single(sink.Items);
    }

    [Fact]
    public void Keeps_sql_dependencies_of_other_requests()
    {
        var (processor, sink) = Build();
        var dependency = new DependencyTelemetry { Type = "SQL", Success = true };
        dependency.Context.Operation.Name = "GET Prints/GetPrintSummary";

        processor.Process(dependency);

        Assert.Single(sink.Items);
    }

    [Fact]
    public void Keeps_non_sql_dependencies_of_the_poll()
    {
        // Nothing but SQL is expected under the poll; if something else appears there it is
        // news, not noise.
        var (processor, sink) = Build();
        var dependency = new DependencyTelemetry { Type = "HTTP", Success = true };
        dependency.Context.Operation.Name = NoiseTelemetryProcessor.UnreadCountRequestName;

        processor.Process(dependency);

        Assert.Single(sink.Items);
    }

    [Fact]
    public void Never_drops_exceptions_or_events()
    {
        var (processor, sink) = Build();
        var exception = new ExceptionTelemetry(new InvalidOperationException("x"));
        exception.Context.Operation.Name = NoiseTelemetryProcessor.UnreadCountRequestName;
        var evt = new EventTelemetry("PrintAdded");
        evt.Context.Operation.Name = NoiseTelemetryProcessor.UnreadCountRequestName;

        processor.Process(exception);
        processor.Process(evt);

        Assert.Equal(2, sink.Items.Count);
    }
}
