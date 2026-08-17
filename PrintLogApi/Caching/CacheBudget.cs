namespace PrintLogApi.Caching;

/// <summary>
/// The single in-process cache budget, and the unit every entry is charged in.
///
/// There is exactly one <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/> in the
/// process and <c>HybridCache</c> stores its L1 entries in that same instance rather than
/// standing up a second one, so <see cref="SizeLimitBytes"/> is the whole ceiling — there is no
/// second, untracked budget to reason about. Verified against
/// Microsoft.Extensions.Caching.Hybrid 10.0.0.
///
/// <para><b>The unit is bytes, and that is not a free choice.</b> HybridCache charges the
/// serialized length of the payload as the entry's <c>Size</c>; it is not configurable. Anything
/// still writing to <c>IMemoryCache</c> directly therefore has to charge bytes too, or the two
/// halves of one budget are denominated differently and the limit means nothing. The previous
/// limit was 8192 in nominal ~1KB "units"; <see cref="SizeLimitBytes"/> is the same intended
/// ceiling, written in the unit that is actually enforced.
///
/// Leaving the old 8192 in place while adopting HybridCache would have capped the entire cache
/// at 8 KB — a single print summary exceeds that, so nothing would ever stay resident.</para>
/// </summary>
public static class CacheBudget
{
    /// <summary>
    /// Total in-process cache ceiling, in bytes, shared by HybridCache's L1 and every remaining
    /// direct <c>IMemoryCache</c> writer.
    /// </summary>
    public const long SizeLimitBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Charge for a small bookkeeping entry written directly to <c>IMemoryCache</c> — a version
    /// GUID, a counter, a throttle flag. Covers the key string plus a boxed scalar or a tiny
    /// object with generous headroom, so it is an over-estimate rather than an under-estimate.
    ///
    /// Precision is not the point: these entries are unbounded in <i>count</i> (one per active
    /// user, one per API key, one per remote address), so what matters is that they are charged
    /// something proportional to real memory instead of a flat 1. At the old flat rate ten
    /// thousand version entries billed 10 KB against a budget while occupying well over a
    /// megabyte, and were consequently never a compaction candidate.
    /// </summary>
    public const long SmallEntryBytes = 128;
}
