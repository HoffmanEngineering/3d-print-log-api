namespace PrintLogApi.Services;

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
}
