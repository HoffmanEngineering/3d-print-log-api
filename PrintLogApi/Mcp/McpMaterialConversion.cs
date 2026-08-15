#nullable enable

using System;

namespace PrintLogApi.Mcp
{
    /// <summary>
    /// The pre-cast overflow guard for material capacity conversions.
    /// <para>
    /// <see cref="Services.MeasurementUtilities"/> ends its conversions with an UNCHECKED
    /// <c>(long)</c> cast. In C# an unchecked double->long cast of an out-of-range value does not
    /// throw — it yields an unspecified result — so a caller-supplied density or amount could store
    /// a nonsense capacity instead of being rejected. Every MCP capacity conversion therefore
    /// range-checks the double here and converts through this method, never by casting directly.
    /// </para>
    /// </summary>
    public static class McpMaterialConversion
    {
        /// <summary>
        /// The smallest double that is NOT representable as a long. long.MaxValue itself rounds up to
        /// 2^63 when widened to double, so comparing against it would let 2^63 through.
        /// </summary>
        private const double ExclusiveMaxMg = 9.2233720368547758E18;

        /// <summary>
        /// Converts to milligrams, rejecting anything unrepresentable.
        /// <para>
        /// <paramref name="minMg"/> guards the low end and MUST be checked against the ROUNDED value:
        /// a capacity of 0.4 mg rounds to 0, and a stored 0 is not "rejected", it is a material that
        /// claims a tracked capacity of nothing. Pass <c>minMg: 1</c> for any figure where zero is
        /// meaningless (capacity); leave it 0 where zero is a real answer (a spool weight of 0).
        /// </para>
        /// </summary>
        public static long RequireMgInRange(double mg, string field, double minMg = 0)
        {
            var rounded = Math.Round(mg);
            if (!double.IsFinite(rounded) || rounded < minMg || rounded >= ExclusiveMaxMg)
            {
                throw McpToolException.InvalidArguments(
                    $"{field} converts to a weight outside the recordable range.");
            }
            return (long)rounded;
        }

        public static long GramsToMg(double grams, string field, double minMg = 0) =>
            RequireMgInRange(grams * 1000.0, field, minMg);
    }
}
