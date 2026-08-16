namespace PrintLogApi.Caching;

/// <summary>
/// Runs a HybridCache factory in a dependency-injection scope it owns, so a shared computation
/// does not outlive the request that happened to start it.
///
/// <para><b>Why this exists.</b> Stampede protection means one caller's factory produces the
/// value that every other caller on that key receives. That caller is an ordinary HTTP request,
/// and its scoped services — <c>PrintLogContext</c> above all — are disposed when its pipeline
/// unwinds. If it aborts mid-computation (a closed tab, a dropped mobile connection) the shared
/// work is left querying through a disposed context, and the joiners waiting on that key get an
/// <c>ObjectDisposedException</c> for a request of their own that was perfectly healthy.</para>
///
/// <para>That hazard is created by stampede protection, not merely exposed by it: before #68
/// every caller ran its own query on its own scope, so an abort could only ever harm the caller
/// that aborted. Resolving services from a scope owned by the computation restores that
/// property — the blast radius of an abort is again one request.</para>
///
/// <para><b>The cancellation token is the same argument.</b> Pass the token HybridCache supplies
/// to the factory, never the originating request's. HybridCache cancels its token only once
/// every joiner has abandoned the key; the request's token cancels the moment that one caller
/// aborts, which propagates its cancellation to everyone else waiting. Verified against
/// Microsoft.Extensions.Caching.Hybrid 10.0.0: with the request token, an aborted winner leaves
/// a healthy joiner with a TaskCanceledException; with HybridCache's token, the joiner is
/// unaffected. <c>CachingConfigurationTests</c> pins this.</para>
///
/// <para>The cost is one extra <c>DbContext</c> per cache miss. Misses are the case that was
/// already about to run a database query, and there is now at most one of them per key.</para>
/// </summary>
public sealed class CachedComputation(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// Invokes <paramref name="load"/> against a service provider scoped to this computation.
    /// Do not let the returned value close over anything from that scope — the scope is disposed
    /// before the result is cached.
    /// </summary>
    public async ValueTask<T> RunAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> load, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await load(scope.ServiceProvider, cancellationToken);
    }
}
