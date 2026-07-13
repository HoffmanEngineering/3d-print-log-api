namespace PrintLogApi.Mcp
{
    /// <summary>
    /// Records MCP tool invocations. Implementations must emit only non-sensitive fields:
    /// the tool name, an outcome, a duration, and a stable hashed subject — never raw user ids,
    /// arguments, notes, material names/colors, or tool results.
    /// </summary>
    public interface IMcpToolTelemetry
    {
        void ToolCalled(string toolName, string outcome, long durationMs, string subjectHash);
    }
}
