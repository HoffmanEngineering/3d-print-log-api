using System.Threading.RateLimiting;
using PrintLogApi.Authentication;

namespace PrintLogApi.Middleware;

/// <summary>
/// The per-credential budget on connector writes (#151, spec 1.8). A bridge backfilling its
/// history can post hundreds of prints in minutes; this bounds each credential to a steady rate
/// with room for a burst, and answers the excess with <c>429</c> and <c>Retry-After</c>, which the
/// bridge honours.
///
/// What is counted: every POST, PUT, PATCH and DELETE under <c>/api</c> (and the Spoolman API's
/// <c>/spoolman/api</c>) made by a request carrying
/// <see cref="ConnectorCredentialClaim"/>. Reads are not counted, and neither is anything an
/// interactive user does, so web app traffic is never affected. Both remain subject to the
/// ordinary per-user "api" budget, which this runs alongside, not instead of.
///
/// Partitioned on the credential, not the user: two bridges of one user each get their own
/// budget, so a backfill on one cannot stall the other's heartbeats. Device tokens (#148) join by
/// adding the same claim.
///
/// It is a token bucket rather than the fixed windows used elsewhere, because the spec asks for a
/// rate with a burst: <c>Burst</c> tokens to start, refilled one at a time at <c>PerMinute</c>. A
/// fixed window would let a client spend two windows' worth across a boundary.
///
/// Wired as part of the global limiter, not as an endpoint policy, because an endpoint gets
/// exactly one policy and the "api" policy already holds that slot; the global limiter is the
/// supported way to apply a second, independent budget.
/// </summary>
public static class ConnectorWriteRateLimiting
{
    public const string PerMinuteKey = "Api:ConnectorWrites:PerMinute";
    public const string BurstKey = "Api:ConnectorWrites:Burst";

    public const int DefaultPerMinute = 60;
    public const int DefaultBurst = 20;

    private static readonly PathString ApiPath = new("/api");

    /// <summary>
    /// The Spoolman-compatible API (#176) lives at <c>/spoolman/api/v1</c>, outside <c>/api</c>,
    /// because Moonraker appends <c>/api/v1</c> to the base URL it is given. Its writes are
    /// connector writes all the same.
    /// </summary>
    private static readonly PathString SpoolmanApiPath = new("/spoolman/api");

    /// <summary>
    /// Builds the limiter. Configuration is read per request, as the other budgets do, so a
    /// factory override in tests applies. <c>PerMinute</c> or <c>Burst</c> of 0 or less disables it.
    /// </summary>
    public static PartitionedRateLimiter<HttpContext> CreateLimiter(IConfiguration configuration)
        => PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var perMinute = configuration.GetValue(PerMinuteKey, DefaultPerMinute);
            var burst = configuration.GetValue(BurstKey, DefaultBurst);

            if (perMinute <= 0
                || burst <= 0
                || !IsWrite(httpContext.Request.Method)
                || !(httpContext.Request.Path.StartsWithSegments(ApiPath) || httpContext.Request.Path.StartsWithSegments(SpoolmanApiPath))
                || ConnectorCredentialClaim.Get(httpContext.User) is not { } credential)
            {
                return RateLimitPartition.GetNoLimiter("not-connector-write");
            }

            return RateLimitPartition.GetTokenBucketLimiter($"connector-write:{credential}", _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = burst,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1) / perMinute,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        });

    private static bool IsWrite(string method)
        => HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
}
