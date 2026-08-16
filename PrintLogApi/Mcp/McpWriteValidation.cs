using System;
using System.Collections.Generic;

namespace PrintLogApi.Mcp
{
    /// <summary>
    /// Input-bound validation shared by the write MCP tools. Every failure is an
    /// <see cref="McpToolException.InvalidArguments"/> so it reaches the client as a safe,
    /// typed error rather than a raw database exception.
    /// </summary>
    public static class McpWriteValidation
    {
        /// <summary>
        /// Sanity bound on any single amount (grams / mm / ml). Well below the point where a converted
        /// milligram value could overflow the int column, so extreme agent input is rejected with a
        /// clear error rather than silently overflowing or corrupting inventory.
        /// </summary>
        public const double MaxAmountMagnitude = 2_000_000d;

        public static double RequireFiniteAmount(double amount)
        {
            if (double.IsNaN(amount) || double.IsInfinity(amount))
            {
                throw McpToolException.InvalidArguments("amount must be a finite number.");
            }
            if (System.Math.Abs(amount) > MaxAmountMagnitude)
            {
                throw McpToolException.InvalidArguments(
                    $"amount magnitude must not exceed {MaxAmountMagnitude:N0} (g / mm / ml).");
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

        public static int RequirePositiveDuration(int seconds, string field)
        {
            if (seconds <= 0)
            {
                throw McpToolException.InvalidArguments($"{field} must be greater than zero.");
            }
            return seconds;
        }

        /// <summary>
        /// Normalizes a caller-supplied clear-field list against the fields a tool actually allows
        /// clearing. An unknown name is rejected rather than ignored, so a typo can never silently
        /// leave a field unchanged when the caller believed it was cleared.
        /// </summary>
        public static ISet<string> RequireAllowedClearFields(string[]? clear, ISet<string> allowed)
        {
            var result = new HashSet<string>();
            if (clear is null)
            {
                return result;
            }
            foreach (var raw in clear)
            {
                var name = raw?.Trim();
                if (string.IsNullOrEmpty(name) || !allowed.Contains(name))
                {
                    throw McpToolException.InvalidArguments($"'{raw}' is not a clearable field.");
                }
                result.Add(name);
            }
            return result;
        }

        public static double RequirePositiveDensity(double density)
        {
            if (double.IsNaN(density) || double.IsInfinity(density) || density <= 0)
            {
                throw McpToolException.InvalidArguments("density must be a finite value greater than zero.");
            }
            return density;
        }

        public static string? RequireMaxLength(string? value, int max, string field)
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
