using System;

namespace PrintLogApi.Mcp
{
    /// <summary>
    /// Input-bound validation shared by the date-range MCP tools. Ranges are inclusive UTC,
    /// require <c>from &lt;= to</c>, and span at most 366 days; violations are rejected rather
    /// than silently clamped.
    /// </summary>
    public static class McpValidation
    {
        public const int MaxRangeDays = 366;

        /// <summary>
        /// Validates an inclusive UTC date range and returns it normalized to UTC.
        /// </summary>
        public static (DateTimeOffset From, DateTimeOffset To) RequireUtcRange(DateTimeOffset from, DateTimeOffset to)
        {
            if (to < from)
            {
                throw McpToolException.InvalidArguments("'to' must be on or after 'from'.");
            }

            if ((to - from) > TimeSpan.FromDays(MaxRangeDays))
            {
                throw McpToolException.InvalidArguments($"Date range must not exceed {MaxRangeDays} days.");
            }

            return (from.ToUniversalTime(), to.ToUniversalTime());
        }

        /// <summary>
        /// Rejects a non-finite or non-positive required amount (grams).
        /// </summary>
        public static double RequirePositiveGrams(double requiredGrams)
        {
            if (double.IsNaN(requiredGrams) || double.IsInfinity(requiredGrams) || requiredGrams <= 0)
            {
                throw McpToolException.InvalidArguments("requiredGrams must be a finite value greater than zero.");
            }

            return requiredGrams;
        }
    }
}
