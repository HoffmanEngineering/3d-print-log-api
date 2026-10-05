using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Email;

/// <summary>Request header carrying an email token to the page endpoints (never the URL, never the body).</summary>
public static class EmailTokenHeader
{
    public const string Name = "X-Email-Token";
}

/// <summary>
/// Budgets for the anonymous email endpoints. Partitioned by token, not client IP: the API does not
/// process forwarded headers, so on App Service every anonymous caller shares one socket peer, and
/// Gmail's one-click POSTs come from a handful of Google addresses anyway (spec §6.8).
/// </summary>
public static class EmailRateLimiting
{
    public const string TokenPolicy = "email-token";
    public const string EventsPolicy = "email-events";

    /// <summary>Per-token budget. The partition key is a hash, so the limiter never holds raw tokens.</summary>
    public sealed class TokenPolicyImpl(IOptions<EmailOptions> options) : IRateLimiterPolicy<string>
    {
        public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

        public RateLimitPartition<string> GetPartition(HttpContext httpContext)
        {
            var limit = options.Value.RateLimits.PerTokenPerMinute;
            if (limit <= 0)
            {
                return RateLimitPartition.GetNoLimiter("email-token-unlimited");
            }

            var token = httpContext.Request.Query["t"].FirstOrDefault()
                ?? httpContext.Request.Headers[EmailTokenHeader.Name].FirstOrDefault()
                ?? "none";
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

            return RateLimitPartition.GetFixedWindowLimiter($"email-token:{key}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        }
    }

    /// <summary>One budget for the SES event webhook, whoever calls it.</summary>
    public sealed class EventsPolicyImpl(IOptions<EmailOptions> options) : IRateLimiterPolicy<string>
    {
        public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

        public RateLimitPartition<string> GetPartition(HttpContext httpContext)
        {
            var limit = options.Value.RateLimits.EventsPerMinute;
            return limit <= 0
                ? RateLimitPartition.GetNoLimiter("email-events-unlimited")
                : RateLimitPartition.GetFixedWindowLimiter("email-events", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
        }
    }

    /// <summary>
    /// A ceiling across every caller of /api/email, so a flood of distinct (invalid) tokens cannot
    /// sidestep the per-token budget. Other paths are not limited here.
    /// </summary>
    public static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter(IConfiguration configuration)
        => PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var limit = configuration.GetValue($"{EmailOptions.SectionName}:RateLimits:GlobalPerMinute", 300);
            if (limit <= 0 || !httpContext.Request.Path.StartsWithSegments("/api/email"))
            {
                return RateLimitPartition.GetNoLimiter("not-email");
            }

            return RateLimitPartition.GetFixedWindowLimiter("email-global", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        });
}
