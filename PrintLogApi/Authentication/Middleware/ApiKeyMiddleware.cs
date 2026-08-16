using System;
using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using PrintLogApi.Services;

namespace PrintLogApi.Authentication.Middleware
{
    public class ApiKeyMiddleware
    {
        private static readonly PathString _apiPath = new("/api");
        private const string FailedAttemptCachePrefix = "apikey_failed:";

        private readonly RequestDelegate _next;
        private readonly IMemoryCache _cache;
        private readonly int _maxFailedAttemptsPerMinute;

        public ApiKeyMiddleware(RequestDelegate next, IMemoryCache cache, IConfiguration configuration)
        {
            _next = next;
            _cache = cache;
            _maxFailedAttemptsPerMinute = configuration.GetValue("Api:InvalidApiKeyAttemptsPerMinute", 20);
        }

        public async Task InvokeAsync(HttpContext context, IUserApiKeyService userApiKeyService)
        {
            if (context.Request.Path.StartsWithSegments(_apiPath))
            {
                if (context.Request.Headers.TryGetValue("X-Api-Key", out var headerKey))
                {
                    await ValidateApiKey(context, _next, headerKey, userApiKeyService);
                }
                else if (context.Request.Query.TryGetValue("api_key", out var queryKey))
                {
                    await ValidateApiKey(context, _next, queryKey, userApiKeyService);
                }
                else
                {
                    await _next.Invoke(context);
                }
            }
            else
            {
                await _next.Invoke(context);
            }
        }

        private async Task ValidateApiKey(HttpContext context, RequestDelegate next, string? key, IUserApiKeyService userApiKeyService)
        {
            long userId;
            try
            {
                userId = await userApiKeyService.GetUserIdByApiKey(key);
            }
            catch (ApiKeyIsNotValidException)
            {
                // Key guessing has to be throttled HERE, not by the "api" rate limiting policy.
                // This middleware short-circuits with a 401 and never calls next, and UseRateLimiter
                // sits further down the pipeline — so a rejected key consumes no budget and every
                // guess reaches the database lookup. Moving UseRateLimiter earlier is not an option:
                // /mcp deliberately depends on running after authorization so that unauthenticated
                // traffic is rejected without spending an authenticated user's budget.
                if (RegisterFailedAttempt(context))
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    context.Response.Headers.RetryAfter = "60";
                    await context.Response.WriteAsync("Too many invalid API key attempts.");
                    return;
                }

                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await context.Response.WriteAsync("Invalid API Key");
                return;
            }

            var identity = new GenericIdentity("API");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
            context.User = new GenericPrincipal(identity, new[] { "ApiUser" });

            await userApiKeyService.UpdateApiKeyLastUsed(key);

            await next(context);
        }

        /// <summary>
        /// Records one rejected key for the calling address and reports whether that address has
        /// now exceeded its per-minute allowance. Only failures count — a caller with a valid key
        /// never accumulates, so a busy legitimate integration is unaffected no matter its volume.
        ///
        /// Partitioned on the socket peer, which carries the same caveat as the anonymous rate
        /// limiting budget: if the App Service front end terminates the connection, every caller
        /// shares one counter. Set Api:InvalidApiKeyAttemptsPerMinute to 0 to disable.
        /// </summary>
        private bool RegisterFailedAttempt(HttpContext context)
        {
            if (_maxFailedAttemptsPerMinute <= 0)
            {
                return false;
            }

            var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // GetOrCreate is declared to return TItem? because a factory may return null; ours
            // always returns a counter, so the result is never null.
            var counter = _cache.GetOrCreate(FailedAttemptCachePrefix + address, entry =>
            {
                // The shared IMemoryCache is size-limited, so every entry must declare a size.
                entry.SetSize(1);
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
                return new FailedAttemptCounter();
            })!;

            return Interlocked.Increment(ref counter.Count) > _maxFailedAttemptsPerMinute;
        }

        /// <summary>
        /// Mutable box so the count can be incremented atomically in place. Storing a bare int
        /// would mean a read-modify-write through the cache, which races under concurrent guesses.
        /// </summary>
        private sealed class FailedAttemptCounter
        {
            public int Count;
        }
    }
}
