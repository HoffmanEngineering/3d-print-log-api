using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using PrintLogApi.Telemetry;
using Xunit;

namespace PrintLogApi.IntegrationTests.Telemetry;

public class SqlCommandTextTelemetryProcessorTests
{
    private const string CommandText = "SELECT [p].[Id] FROM [Prints] AS [p] WHERE [p].[UserId] = @__userId_0";

    private sealed class Sink : ITelemetryProcessor
    {
        public List<ITelemetry> Items { get; } = new();
        public void Process(ITelemetry item) => Items.Add(item);
    }

    private static (SqlCommandTextTelemetryProcessor processor, Sink sink) Build()
    {
        var sink = new Sink();
        return (new SqlCommandTextTelemetryProcessor(sink), sink);
    }

    private static DependencyTelemetry Dependency(string type, int milliseconds, bool? success = true)
        => new()
        {
            Type = type,
            Name = "printlog.database.windows.net | PrintLogDb",
            Data = CommandText,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            Success = success,
        };

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(499)]
    public void Clears_the_text_of_a_fast_successful_sql_call(int milliseconds)
    {
        var (processor, sink) = Build();

        processor.Process(Dependency("SQL", milliseconds));

        var forwarded = Assert.IsType<DependencyTelemetry>(Assert.Single(sink.Items));
        Assert.True(string.IsNullOrEmpty(forwarded.Data));
        // The row itself, with its target and duration, is still sent.
        Assert.Equal("printlog.database.windows.net | PrintLogDb", forwarded.Name);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(4_000)]
    public void Keeps_the_text_of_a_slow_sql_call(int milliseconds)
    {
        var (processor, sink) = Build();

        processor.Process(Dependency("SQL", milliseconds));

        var forwarded = Assert.IsType<DependencyTelemetry>(Assert.Single(sink.Items));
        Assert.Equal(CommandText, forwarded.Data);
    }

    [Fact]
    public void Keeps_the_text_of_a_failed_sql_call()
    {
        var (processor, sink) = Build();

        processor.Process(Dependency("SQL", 3, success: false));

        var forwarded = Assert.IsType<DependencyTelemetry>(Assert.Single(sink.Items));
        Assert.Equal(CommandText, forwarded.Data);
    }

    [Theory]
    [InlineData("HTTP")]
    [InlineData("Azure blob")]
    public void Leaves_other_dependencies_untouched(string type)
    {
        var (processor, sink) = Build();

        processor.Process(Dependency(type, 3));

        var forwarded = Assert.IsType<DependencyTelemetry>(Assert.Single(sink.Items));
        Assert.Equal(CommandText, forwarded.Data);
    }

    [Fact]
    public void Forwards_everything_else_unchanged()
    {
        var (processor, sink) = Build();
        var request = new RequestTelemetry { Name = "GET Prints/GetPrintSummary" };

        processor.Process(request);

        Assert.Same(request, Assert.Single(sink.Items));
    }
}
