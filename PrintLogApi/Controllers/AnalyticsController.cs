using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
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
    IMemoryCache cache,
    ICacheVersionService cacheVersionService) : ControllerBase
{
    // Correctness comes from the per-user cache version, not from this window. The TTL only
    // stops the cache growing without bound.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Every analytics endpoint validates, caches and authorizes identically. Written once so
    /// a sixth endpoint cannot quietly omit the tenant from its cache key.
    ///
    /// The key includes the tenant, the per-user cache version (bumped by every mutation,
    /// exactly as PrintsController does) and every normalized filter value. A TTL alone
    /// would serve stale analytics right after a user logs a print — the single most
    /// likely moment for them to open this page.
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
        // Null-forgiven: only `load`'s non-null result is ever stored under this key.
        if (cache.TryGetValue(cacheKey, out T? cached)) return cached!;

        var result = await load(userId.Value, filter);

        cache.Set(cacheKey, result, new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(CacheTtl)
            .SetSize(1)
            .SetPriority(CacheItemPriority.Low));

        return result;
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
