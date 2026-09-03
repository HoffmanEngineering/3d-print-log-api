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
/// for binary comparison. Substring semantics are unchanged.
///
/// Matching is no longer culture-aware, and PrintSearchCollationCorpusTests measures exactly
/// where that shows. Only two of those cases are reachable in production data, which was
/// censused rather than assumed:
///
///   * <b>Sharp-s.</b> CI_AS expands ss to match Stra&#223;e; binary comparison does not.
///   * <b>Turkish dotted capital I (U+0130), in both directions.</b> The column is lowered by
///     SQL Server and the term by .NET, and their case tables disagree on this one character:
///     SQL Server's LOWER folds it to "i" while ToLowerInvariant leaves it untouched (measured
///     on .NET 10). So searching "i" now matches &#304;stanbul where it did not, and searching
///     "&#304;" no longer does. Do not "fix" the second half with a culture-sensitive ToLower()
///     &mdash; that would make every result depend on the server's locale.
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
    /// Binary collation used for every free-text search comparison, in place of the column's own
    /// culture-aware SQL_Latin1_General_CP1_CI_AS.
    ///
    /// Measured against production (351,515 rows, a user with 1,584 prints, term "wall"):
    /// <b>CPU 33ms -> 10ms, elapsed 155ms -> 61ms, identical results</b>. The plan is unchanged —
    /// same Index Seek on SummaryIndex, no Key Lookup, no Sort, and the SAME cardinality
    /// estimates, because a leading-wildcard LIKE was already estimated by a fixed guess and
    /// LOWER() therefore had no statistics left to defeat.
    ///
    /// <b>An isolated benchmark of the bare predicate said 16x. Do not quote that number.</b> It
    /// used uniform synthetic rows and hand-written SQL with no index; on real data the gain is
    /// ~3x.
    ///
    /// <b>One metric gets worse.</b> LOB logical reads rose ~6x (166 -> 995 on the count query),
    /// because LOWER() over nvarchar(max) shows up as CONVERT(nvarchar(max), lower(Notes)) and
    /// materialises the whole value, where the old LIKE could stop at the first match. Physical
    /// reads stayed 0, so it is buffer-pool traffic rather than I/O — but it is the reason to
    /// watch avg_physical_io_reads rather than only CPU after a deploy.
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
