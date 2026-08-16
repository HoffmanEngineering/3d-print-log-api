using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Caching;
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
    HybridCache cache,
    CachedComputation computation,
    ICacheVersionService cacheVersionService,
    TimeProvider timeProvider) : ControllerBase
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
    /// <para>The analytics service comes from <see cref="CachedComputation"/>'s scope and the
    /// query runs on the token HybridCache supplies, never on the calling action's. Both matter
    /// once one caller's factory serves every caller on the key: the originating request may
    /// abort while the others are still waiting, and its token and its DbContext would take
    /// them down with it. Read CachedComputation before changing either.</para>
    /// </summary>
    private async Task<ActionResult<T>> Cached<T>(
        string name, AnalyticsFilter filter,
        Func<IServiceProvider, long, AnalyticsFilter, CancellationToken, Task<T>> load) where T : class
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        filter ??= new AnalyticsFilter();
        // The injected clock, not the parameterless overload: Normalize()'s clamp ceiling
        // becomes part of the cache key below, so a test running on a fake clock would
        // otherwise key its entries off the real wall clock and stop being reproducible.
        filter.Normalize(timeProvider.GetUtcNow());

        var errors = filter.Validate();
        if (errors.Count > 0) return BadRequest(new { errors });

        var version = cacheVersionService.GetUserCacheVersion(userId.Value);
        var cacheKey = $"{name}:v{version}:{filter.CacheKey(userId.Value)}";

        return await cache.GetOrCreateAsync(
            cacheKey,
            (userId: userId.Value, filter, load, computation),
            static (state, ct) => state.computation.RunAsync(
                (services, token) => state.load(services, state.userId, state.filter, token), ct),
            CacheTtl,
            cancellationToken: HttpContext.RequestAborted);
    }

    [HttpGet("overview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<ActionResult<OverviewResponse>> GetOverview(
        [FromQuery] AnalyticsFilter filter) =>
        Cached("overview", filter, (sp, userId, f, token) =>
            sp.GetRequiredService<IAnalyticsService>().GetOverview(userId, f, token));

    [HttpGet("activity")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<ActionResult<ActivityResponse>> GetActivity(
        [FromQuery] AnalyticsFilter filter) =>
        Cached("activity", filter, (sp, userId, f, token) =>
            sp.GetRequiredService<IActivityAnalyticsService>().GetActivity(userId, f, token));

    [HttpGet("printers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<ActionResult<PrintersResponse>> GetPrinters(
        [FromQuery] AnalyticsFilter filter) =>
        Cached("printers", filter, (sp, userId, f, token) =>
            sp.GetRequiredService<IPrinterAnalyticsService>().GetPrinters(userId, f, token));

    [HttpGet("materials")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<ActionResult<MaterialsResponse>> GetMaterials(
        [FromQuery] AnalyticsFilter filter) =>
        Cached("materials", filter, (sp, userId, f, token) =>
            sp.GetRequiredService<IMaterialAnalyticsService>().GetMaterials(userId, f, token));

    [HttpGet("costs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<ActionResult<CostsResponse>> GetCosts(
        [FromQuery] AnalyticsFilter filter) =>
        Cached("costs", filter, (sp, userId, f, token) =>
            sp.GetRequiredService<ICostAnalyticsService>().GetCosts(userId, f, token));

    [HttpGet("accuracy")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<ActionResult<AccuracyResponse>> GetAccuracy(
        [FromQuery] AnalyticsFilter filter) =>
        Cached("accuracy", filter, (sp, userId, f, token) =>
            sp.GetRequiredService<IAccuracyAnalyticsService>().GetAccuracy(userId, f, token));
}
