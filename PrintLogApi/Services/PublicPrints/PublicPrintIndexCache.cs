using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Caching;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Services.PublicPrints;

/// <summary>
/// The public print index behind <c>GET api/Prints/public</c> and <c>public/sitemap</c>, read
/// once per <see cref="Lifetime"/> through HybridCache (see AGENTS.md, Caching). That read took
/// 60 s and more on the production database while it scanned Prints (api#309); crawlers and the
/// sitemap generator ask for it far more often than it changes, and both endpoints are anonymous,
/// so a print made public or private shows up within ten minutes, like the five minutes of
/// <c>Cache-Control</c> the responses already carry.
///
/// <para>One key, the same for every caller, so it does not fall under the "anonymous and
/// enumerable keys" rule that keeps the image and <c>/seo</c> endpoints out of HybridCache: a
/// crawler cannot make it grow. Its size is pinned by <c>PublicPrintIndexCacheTests</c>.</para>
/// </summary>
public sealed class PublicPrintIndexCache(HybridCache cache, CachedComputation computation)
{
    public const string CacheKey = "prints:public-index:v1";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private static readonly HybridCacheEntryOptions Options = new()
    {
        Expiration = Lifetime,
        LocalCacheExpiration = Lifetime,
    };

    public async Task<PublicPrintIndex> GetAsync(CancellationToken ct) =>
        await cache.GetOrCreateAsync(
            CacheKey,
            computation,
            static (state, token) => state.RunAsync(
                (services, t) => LoadAsync(services.GetRequiredService<PrintLogContext>(), t), token),
            Options,
            cancellationToken: ct);

    /// <summary>One seek on <c>IX_Prints_ViewStatus</c>, already in id order.</summary>
    public static async Task<PublicPrintIndex> LoadAsync(PrintLogContext db, CancellationToken ct)
    {
        var rows = await db.Prints
            .AsNoTracking()
            .Where(p => p.ViewStatus == PrintViewStatus.Public)
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.UpdatedDate })
            .ToListAsync(ct);

        return new PublicPrintIndex(
            rows.Select(r => r.Id).ToArray(),
            rows.Select(r => r.UpdatedDate.Ticks).ToArray());
    }
}
