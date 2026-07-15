using System;

namespace PrintLogApi.Mcp
{
    /// <summary>
    /// Input-bound validation shared by the write MCP tools. Every failure is an
    /// <see cref="McpToolException.InvalidArguments"/> so it reaches the client as a safe,
    /// typed error rather than a raw database exception.
    /// </summary>
    public static class McpWriteValidation
    {
        public static double RequireFiniteAmount(double amount)
        {
            if (double.IsNaN(amount) || double.IsInfinity(amount))
            {
                throw McpToolException.InvalidArguments("amount must be a finite number.");
            }
            return amount;
        }

        public static double RequirePositiveAmount(double amount)
        {
            RequireFiniteAmount(amount);
            if (amount <= 0)
            {
                throw McpToolException.InvalidArguments("amount must be greater than zero.");
            }
            return amount;
        }

        public static int RequirePositiveDuration(int seconds)
        {
            if (seconds <= 0)
            {
                throw McpToolException.InvalidArguments("durationSeconds must be greater than zero.");
            }
            return seconds;
        }

        public static double RequirePositiveDensity(double density)
        {
            if (double.IsNaN(density) || double.IsInfinity(density) || density <= 0)
            {
                throw McpToolException.InvalidArguments("density must be a finite value greater than zero.");
            }
            return density;
        }

        public static string RequireMaxLength(string value, int max, string field)
        {
            if (value != null && value.Length > max)
            {
                throw McpToolException.InvalidArguments($"{field} must be at most {max} characters.");
            }
            return value;
        }

        public static T RequireDefinedEnum<T>(T value, string field) where T : struct, Enum
        {
            if (!Enum.IsDefined(value))
            {
                throw McpToolException.InvalidArguments($"{field} is not a valid value.");
            }
            return value;
        }
    }
}
