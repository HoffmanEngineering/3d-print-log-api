#nullable enable

using System;
using System.Linq.Expressions;
using System.Reflection;
using PrintLogApi.Models;

namespace PrintLogApi.Mcp
{
    /// <summary>
    /// Word-boundary text matching for the free-text <see cref="Filament.MaterialType"/> and
    /// <see cref="Filament.ColorName"/> fields.
    ///
    /// Exact matching is wrong for roughly a third of real inventory: "PLA" must also find
    /// "PLA (Polylactic Acid)", "PLA+" and "Silk PLA", and "blue" must also find "Light Blue".
    /// Normalization maps a frozen separator set to spaces, collapses runs, and matches on padded
    /// containment, so "PC" still does not match "PCTG".
    ///
    /// The predicates MUST stay EF-translatable: only <c>Replace(const, const)</c>, <c>ToLower</c>,
    /// <c>Concat</c> and <c>Contains</c> are used. Regex, char loops, or string.Join would throw at
    /// query time on EF Core 10 rather than silently evaluating on the client.
    /// </summary>
    public static class McpTextMatch
    {
        public const int MaxFilterLength = 100;

        /// <summary>
        /// Frozen v1 separator set. Every entry is replaced with a space before matching, which is
        /// what lets "PLA" match "PLA+" and "PLA-CF". Changing this changes matching behaviour for
        /// every user; treat it as a contract.
        /// </summary>
        private static readonly string[] Separators =
        {
            "\t", "\n", "\r",
            "+", "-", "/", "\\", "|", "(", ")", "[", "]", "{", "}",
            ".", ",", ":", ";", "_", "&", "'", "\"", "#", "%", "@", "*", "!", "?", "=", "~", "^", "$",
            "–", // en dash
            "—", // em dash
            " ", // non-breaking space
        };

        /// <summary>
        /// Each pass halves a run of spaces, so k passes collapse a run of up to 2^k. Stored
        /// MaterialType/ColorName are StringLength(255) and filters are capped at 100, so the
        /// worst-case run is 255 characters: 8 passes (2^8 = 256) fully collapse it. Fewer passes
        /// would leave multi-space gaps and silently break multi-token matching such as
        /// "PLA-CF" against a stored "PLA--CF".
        /// </summary>
        private const int CollapsePasses = 8;

        private static readonly MethodInfo ToLowerMethod =
            typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

        private static readonly MethodInfo ReplaceMethod =
            typeof(string).GetMethod(nameof(string.Replace), new[] { typeof(string), typeof(string) })!;

        private static readonly MethodInfo ContainsMethod =
            typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!;

        private static readonly MethodInfo ConcatMethod =
            typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string), typeof(string) })!;

        /// <summary>
        /// In-memory normalization, used to build the needle. Mirrors <see cref="BuildNormalized"/>
        /// except for the trailing <c>Trim</c>, which is deliberate: the needle is padded with
        /// single spaces, so a query of "+PLA" must normalize to "pla" and not " pla".
        /// </summary>
        public static string Normalize(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var normalized = value.ToLower();
            foreach (var separator in Separators)
            {
                normalized = normalized.Replace(separator, " ");
            }

            for (var i = 0; i < CollapsePasses; i++)
            {
                normalized = normalized.Replace("  ", " ");
            }

            return normalized.Trim();
        }

        /// <summary>
        /// Validates a caller-supplied filter. A filter that normalizes to nothing (for example "+"
        /// or "---") would make the padded match succeed against every row, so it is rejected rather
        /// than treated as "no filter".
        /// </summary>
        public static string RequireFilter(string value, string paramName)
        {
            if (value is { Length: > MaxFilterLength })
            {
                throw McpToolException.InvalidArguments(
                    $"{paramName} must be {MaxFilterLength} characters or fewer.");
            }

            var normalized = Normalize(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw McpToolException.InvalidArguments(
                    $"{paramName} must contain at least one searchable character.");
            }

            return normalized;
        }

        public static Expression<Func<Filament, bool>> MaterialMatches(string query) =>
            BuildPredicate(query, nameof(Filament.MaterialType));

        public static Expression<Func<Filament, bool>> ColorMatches(string query) =>
            BuildPredicate(query, nameof(Filament.ColorName));

        private static Expression<Func<Filament, bool>> BuildPredicate(string query, string propertyName)
        {
            var needle = Expression.Constant(" " + RequireFilter(query, propertyName) + " ");
            var parameter = Expression.Parameter(typeof(Filament), "f");
            var field = Expression.Property(parameter, propertyName);
            var space = Expression.Constant(" ");

            var padded = Expression.Call(ConcatMethod, space, BuildNormalized(field), space);
            var contains = Expression.Call(padded, ContainsMethod, needle);

            // A NULL column flows through REPLACE/CHARINDEX as NULL in SQL, so the predicate is
            // simply false for it. No null guard is needed (and adding one would not translate).
            return Expression.Lambda<Func<Filament, bool>>(contains, parameter);
        }

        /// <summary>
        /// Emits <c>LOWER</c> plus the nested <c>REPLACE</c> chain. Kept structurally identical to
        /// <see cref="Normalize"/> (same separators, same pass count) — if the two ever diverge,
        /// matching breaks silently.
        /// </summary>
        private static Expression BuildNormalized(Expression source)
        {
            Expression expression = Expression.Call(source, ToLowerMethod);

            foreach (var separator in Separators)
            {
                expression = Expression.Call(
                    expression, ReplaceMethod, Expression.Constant(separator), Expression.Constant(" "));
            }

            for (var i = 0; i < CollapsePasses; i++)
            {
                expression = Expression.Call(
                    expression, ReplaceMethod, Expression.Constant("  "), Expression.Constant(" "));
            }

            return expression;
        }
    }
}
