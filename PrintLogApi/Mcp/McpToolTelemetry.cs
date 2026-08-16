using System.Globalization;
using Microsoft.ApplicationInsights;

namespace PrintLogApi.Mcp;

public sealed class McpToolTelemetry(
    TelemetryClient telemetryClient,
    ILogger<McpToolTelemetry> logger) : IMcpToolTelemetry
{
    public void ToolCalled(string toolName, string outcome, long durationMs, string subjectHash)
    {
        telemetryClient.TrackEvent("Mcp_ToolCalled", new Dictionary<string, string>
        {
            ["tool"] = toolName,
            ["outcome"] = outcome,
            ["durationMs"] = durationMs.ToString(CultureInfo.InvariantCulture),
            ["subjectHash"] = subjectHash,
        });

        logger.LogInformation(
            "Mcp_ToolCalled tool={Tool} outcome={Outcome} durationMs={DurationMs} subject={SubjectHash}",
            toolName, outcome, durationMs, subjectHash);
    }
}
