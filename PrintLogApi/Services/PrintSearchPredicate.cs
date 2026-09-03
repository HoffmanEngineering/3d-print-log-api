using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;

namespace PrintLogApi.Services;

/// <summary>
/// Free-text search matching for prints.
///
/// Every comparison runs as <c>LOWER(col) COLLATE Latin1_General_BIN2 LIKE @t ESCAPE</c>. The
/// column's own SQL_Latin1_General_CP1_CI_AS is culture-aware, which forces a linguistic rules
/// engine at every candidate position of every row — measured at ~58ns/char against ~2.2ns/char
/// for binary comparison. Substring semantics are unchanged; see the spec for the one measured
/// behaviour difference (sharp-s vs ss).
///
/// Three rules hold this together, and each is a trap that compiles and runs when broken:
///
///   * <b>Lower OUTSIDE the collation.</b> Collate(col.ToLower(), C) gives LOWER(col) COLLATE C.
///     The reverse gives LOWER(col COLLATE C), which folds case with a different case table.
///   * <b>Match through .Contains(), never EF.Functions.Like.</b> Contains emits an ESCAPE
///     clause; Like does not, which would turn a search for "50%" into a wildcard query.
///   * <b>ToLowerInvariant on the term, never ToLower.</b> The parameterless overload is
///     CurrentCulture-sensitive, so results would depend on the server's culture (Turkish "I").
///     The term is always lowered in .NET: .Contains() parameterises it, so SQL-side lowering of
///     the term is not reachable without losing the escaping above.
///
/// PrintSearchTranslationTests pins all three against the generated SQL Server SQL.
/// </summary>
public static class PrintSearchPredicate
{
    /// <summary>
    /// Binary collation used for every free-text search comparison. Culture-aware matching under
    /// the column's own SQL_Latin1_General_CP1_CI_AS costs ~58ns/char against ~2.2ns/char here —
    /// 16x in an isolated benchmark of the predicate. That benchmark used uniform synthetic rows
    /// and hand-written SQL, so it bounds the comparison cost rather than predicting the
    /// end-to-end win. See the spec for the semantics trade and the measurement's limits.
    /// </summary>
    public const string BinaryCollation = "Latin1_General_BIN2";

    /// <summary>
    /// Splits a raw search box value into criteria, preserving double-quoted phrases as single
    /// terms. Behaviour is deliberately identical to the two inline copies this replaced,
    /// including the quirk that an empty quoted pair ("") yields an empty criterion which then
    /// matches every non-null field.
    /// </summary>
    public static List<string> SplitCriteria(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return new List<string>();
        }

        return searchText.Split('"')
            .Select((element, index) => index % 2 == 0
                ? element.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                : new[] { element })
            .SelectMany(element => element)
            .ToList();
    }

    /// <summary>Matches a term against a print's title or notes.</summary>
    public static Expression<Func<Print, bool>> TitleOrNotes(string term)
    {
        var needle = term.ToLowerInvariant();

        return p => EF.Functions.Collate(p.Title!.ToLower(), BinaryCollation).Contains(needle)
                 || EF.Functions.Collate(p.Notes!.ToLower(), BinaryCollation).Contains(needle);
    }

    /// <summary>
    /// Matches a term against a print's title, notes, or its project's name. Only for the
    /// grouped feed's project-assigned branch — the standalone branch has ProjectId IS NULL, so
    /// the project term there can never be true and only costs a join.
    /// </summary>
    public static Expression<Func<Print, bool>> TitleNotesOrProjectName(string term)
    {
        var needle = term.ToLowerInvariant();

        return p => EF.Functions.Collate(p.Title!.ToLower(), BinaryCollation).Contains(needle)
                 || EF.Functions.Collate(p.Notes!.ToLower(), BinaryCollation).Contains(needle)
                 || EF.Functions.Collate(p.Project!.Name!.ToLower(), BinaryCollation).Contains(needle);
    }
}
