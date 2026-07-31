using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services;
using PrintLogApi.Services.Analytics;

namespace PrintLogApi.Controllers
{
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
    public class AnalyticsController : ControllerBase
    {
        // Correctness comes from the per-user cache version, not from this window. The TTL only
        // stops the cache growing without bound.
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

        private readonly IAnalyticsService _analytics;
        private readonly IMemoryCache _cache;
        private readonly ICacheVersionService _cacheVersionService;

        public AnalyticsController(
            IAnalyticsService analytics, IMemoryCache cache, ICacheVersionService cacheVersionService)
        {
            _analytics = analytics;
            _cache = cache;
            _cacheVersionService = cacheVersionService;
        }

        [HttpGet("overview")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<OverviewResponse>> GetOverview(
            [FromQuery] AnalyticsFilter filter, CancellationToken ct)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue) return Unauthorized();

            filter ??= new AnalyticsFilter();
            filter.Normalize();

            var errors = filter.Validate();
            if (errors.Count > 0) return BadRequest(new { errors });

            // The key includes the tenant, the per-user cache version (bumped by every mutation,
            // exactly as PrintsController does) and every normalized filter value. A TTL alone
            // would serve stale analytics right after a user logs a print — the single most
            // likely moment for them to open this page.
            var version = _cacheVersionService.GetUserCacheVersion(userId.Value);
            var cacheKey = $"overview:v{version}:{filter.CacheKey(userId.Value)}";
            if (_cache.TryGetValue(cacheKey, out OverviewResponse cached)) return cached;

            var result = await _analytics.GetOverview(userId.Value, filter, ct);

            _cache.Set(cacheKey, result, new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(CacheTtl)
                .SetSize(1)
                .SetPriority(CacheItemPriority.Low));

            return result;
        }
    }
}
