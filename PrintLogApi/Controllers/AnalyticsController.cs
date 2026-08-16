using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services;
using PrintLogApi.Services.Analytics;

namespace PrintLogApi.Controllers;

/// <summary>
/// Read-only analytics aggregates for the authenticated user.
///
/// The tenant comes from the token and nothing else: there is deliberately no userId
/// parameter, unlike /api/Prints/summary. Filter ids the caller does not own simply match
/// nothing, so the endpoint cannot be used to probe whether another user's printer exists.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AnalyticsController(
    IAnalyticsService analytics,
    IActivityAnalyticsService activity,
    IPrinterAnalyticsService printers,
    IMaterialAnalyticsService materials,
    ICostAnalyticsService costs,
    IAccuracyAnalyticsService accuracy,
    HybridCache cache,
    ICacheVersionService cacheVersionService) : ControllerBase
{
    // Correctness comes from the per-user cache version, not from this window. The TTL only
    // stops the cache growing without bound.
    private static readonly HybridCacheEntryOptions CacheTtl = new()
    {
        Expiration = TimeSpan.FromMinutes(15),
        LocalCacheExpiration = TimeSpan.FromMinutes(15),
    };

    /// <summary>
    /// Every analytics endpoint validates, caches and authorizes identically. Written once so
    /// a sixth endpoint cannot quietly omit the tenant from its cache key.
    ///
    /// The key includes the tenant, the per-user cache version (bumped by every mutation,
    /// exactly as PrintsController does) and every normalized filter value. A TTL alone
    /// would serve stale analytics right after a user logs a print — the single most
    /// likely moment for them to open this page.
    ///
    /// <para>Validation runs before the lookup, not inside the factory, and must stay there:
    /// only a successful load is cacheable, and a BadRequest is not a <typeparamref name="T"/>.
    /// The factory is reached only once the request is known to be well-formed and
    /// authorized.</para>
    ///
    /// <para>GetOrCreateAsync gives these six endpoints stampede protection: concurrent misses
    /// on one key run the aggregation once. Every analytics query for a user misses at the same
    /// instant — a version bump invalidates all six tabs together — so this is the site where
    /// the old get/compute/set shape was most exposed.</para>
    ///
    /// <para>The factory ignores the token HybridCache passes it because <paramref name="load"/>
    /// already closes over the calling action's CancellationToken. The token supplied to
    /// GetOrCreateAsync is the one that matters: it governs this caller's wait, and HybridCache
    /// abandons the shared computation only when every joiner has cancelled.</para>
    /// </summary>
    private async Task<ActionResult<T>> Cached<T>(
        string name, AnalyticsFilter filter, Func<long, AnalyticsFilter, Task<T>> load) where T : class
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        filter ??= new AnalyticsFilter();
        filter.Normalize();

        var errors = filter.Validate();
        if (errors.Count > 0) return BadRequest(new { errors });

        var version = cacheVersionService.GetUserCacheVersion(userId.Value);
        var cacheKey = $"{name}:v{version}:{filter.CacheKey(userId.Value)}";

        return await cache.GetOrCreateAsync(
            cacheKey,
            (userId: userId.Value, filter, load),
            static (state, _) => new ValueTask<T>(state.load(state.userId, state.filter)),
            CacheTtl,
            cancellationToken: HttpContext.RequestAborted);
    }

    [HttpGet("overview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<ActionResult<OverviewResponse>> GetOverview(
        [FromQuery] AnalyticsFilter filter, CancellationToken ct) =>
        Cached("overview", filter, (userId, f) => analytics.GetOverview(userId, f, ct));

    [HttpGet("activity")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<ActionResult<ActivityResponse>> GetActivity(
        [FromQuery] AnalyticsFilter filter, CancellationToken ct) =>
        Cached("activity", filter, (userId, f) => activity.GetActivity(userId, f, ct));

    [HttpGet("printers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<ActionResult<PrintersResponse>> GetPrinters(
        [FromQuery] AnalyticsFilter filter, CancellationToken ct) =>
        Cached("printers", filter, (userId, f) => printers.GetPrinters(userId, f, ct));

    [HttpGet("materials")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<ActionResult<MaterialsResponse>> GetMaterials(
        [FromQuery] AnalyticsFilter filter, CancellationToken ct) =>
        Cached("materials", filter, (userId, f) => materials.GetMaterials(userId, f, ct));

    [HttpGet("costs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<ActionResult<CostsResponse>> GetCosts(
        [FromQuery] AnalyticsFilter filter, CancellationToken ct) =>
        Cached("costs", filter, (userId, f) => costs.GetCosts(userId, f, ct));

    [HttpGet("accuracy")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<ActionResult<AccuracyResponse>> GetAccuracy(
        [FromQuery] AnalyticsFilter filter, CancellationToken ct) =>
        Cached("accuracy", filter, (userId, f) => accuracy.GetAccuracy(userId, f, ct));
}
