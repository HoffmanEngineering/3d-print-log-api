namespace PrintLogApi.Mcp;

/// <summary>
/// Invariant unit conversions for MCP tool responses. Weight is grams (from milligrams),
/// rounded to three decimals midpoint-away-from-zero.
/// </summary>
public static class McpUnits
{
    public static double MgToGrams(long? milligrams) =>
        milligrams is null ? 0d : Math.Round(milligrams.Value / 1000d, 3, MidpointRounding.AwayFromZero);

    public static double MgToGrams(int? milligrams) =>
        MgToGrams((long?)milligrams);

    /// <summary>Success rate as a percentage; zero-denominator yields 0, never NaN.</summary>
    public static double SuccessRatePercent(int successful, int total) =>
        total <= 0 ? 0d : Math.Round(successful * 100d / total, 2, MidpointRounding.AwayFromZero);
}
