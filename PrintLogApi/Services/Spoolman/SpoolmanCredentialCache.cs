using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Caching;

namespace PrintLogApi.Services.Spoolman;

public enum SpoolmanCredentialStatus
{
    Unknown = 0,
    Valid = 1,
    Revoked = 2,
    Disabled = 3,
}

/// <summary>What a secret hash resolves to. Immutable, so HybridCache may share the instance.</summary>
[ImmutableObject(true)]
public sealed record SpoolmanCredential(SpoolmanCredentialStatus Status, Guid ConnectionId, long UserId, long? PrinterId)
{
    public static readonly SpoolmanCredential Unknown = new(SpoolmanCredentialStatus.Unknown, Guid.Empty, 0, null);
}

public interface ISpoolmanCredentialCache
{
    /// <summary>Resolves a secret hash, from the cache when it can. Never throws for a bad secret.</summary>
    Task<SpoolmanCredential> ResolveAsync(string secretHash, CancellationToken ct);

    /// <summary>Evicts these secret hashes, so the next request re-reads their connection.</summary>
    Task InvalidateAsync(IEnumerable<string> secretHashes);

    /// <summary>Evicts the secret hashes of these connections.</summary>
    Task InvalidateConnectionsAsync(PrintLogContext context, IEnumerable<Guid> connectionIds);
}

/// <summary>
/// The credential lookup behind every <c>/spoolman</c> request (#176), cached in HybridCache by
/// secret hash, <b>revoked and paused results included</b>, unknown ones never (#193).
///
/// <para>Negative caching is the point. Moonraker retries its websocket every 2 seconds with no
/// backoff, so a printer whose connection was revoked or paused by a downgrade would otherwise
/// cost a query 30 times a minute, indefinitely. Those connections still exist as rows, so their
/// entries are bounded by the rows that exist.</para>
///
/// <para>A secret that matches no row is <b>not</b> cached, like an unknown API key. Those
/// entries would be bounded by nothing but what a caller chooses to send, and they draw on the
/// one in-process budget (<see cref="CacheBudget"/>) that every user's summaries, analytics,
/// claims and API-key lookups share, so a stream of random secrets would evict those. The
/// failed-attempt limiter runs after the lookup (a valid secret is never refused), so it cannot
/// stop the writes either. An unknown secret costs one seek on the unique <c>SecretHash</c>
/// index per attempt instead, a mistyped one in moonraker.conf included.</para>
///
/// <para>Correctness on revoke, rotate, tier change and printer delete comes from explicit
/// eviction by every path that changes a connection, not from the TTL; the TTL only bounds
/// memory. In-process, like every cache here: one App Service instance.</para>
/// </summary>
public sealed class SpoolmanCredentialCache(HybridCache cache, CachedComputation computation) : ISpoolmanCredentialCache
{
    private static readonly HybridCacheEntryOptions Options = new()
    {
        Expiration = TimeSpan.FromHours(1),
        LocalCacheExpiration = TimeSpan.FromHours(1),
    };

    private static string Key(string secretHash) => $"spoolman_credential:{secretHash}";

    public async Task<SpoolmanCredential> ResolveAsync(string secretHash, CancellationToken ct)
    {
        // An unknown hash throws out of the factory, and that is how it stays out of the cache:
        // HybridCache stores nothing when its factory throws, while its entry options are fixed
        // before the factory runs, so nothing inside the factory can say "don't store this one".
        // The alternatives cost more: a read-only probe (DisableUnderlyingData) then a lookup
        // outside GetOrCreateAsync gives up stampede protection for known secrets on a miss, and
        // an uncached pre-check adds a second query to every miss. UserApiKeyService keeps an
        // unknown API key out the same way. Concurrent callers on one unknown hash still share
        // the one query, and each gets the exception, which never leaves this method.
        try
        {
            // The context comes from CachedComputation's scope, and the token is HybridCache's:
            // the result is shared with every concurrent caller on this hash. See CachedComputation.
            return await cache.GetOrCreateAsync(
                Key(secretHash),
                (computation, secretHash),
                static (state, token) => state.computation.RunAsync(async (services, innerToken) =>
                {
                    var row = await services.GetRequiredService<PrintLogContext>().SpoolmanConnections
                        .AsNoTracking()
                        .Where(c => c.SecretHash == state.secretHash)
                        .Select(c => new { c.Id, c.UserId, c.PrinterId, c.RevokedAt, c.DisabledAt })
                        .SingleOrDefaultAsync(innerToken);

                    if (row is null)
                    {
                        throw new UnknownSecretException();
                    }

                    var status = row.RevokedAt.HasValue
                        ? SpoolmanCredentialStatus.Revoked
                        : row.DisabledAt.HasValue ? SpoolmanCredentialStatus.Disabled : SpoolmanCredentialStatus.Valid;
                    return new SpoolmanCredential(status, row.Id, row.UserId, row.PrinterId);
                }, token),
                Options,
                cancellationToken: ct);
        }
        catch (UnknownSecretException)
        {
            return SpoolmanCredential.Unknown;
        }
    }

    public async Task InvalidateAsync(IEnumerable<string> secretHashes)
    {
        foreach (var hash in secretHashes.Distinct())
        {
            await cache.RemoveAsync(Key(hash));
        }
    }

    public async Task InvalidateConnectionsAsync(PrintLogContext context, IEnumerable<Guid> connectionIds)
    {
        var ids = connectionIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var hashes = await context.SpoolmanConnections
            .Where(c => ids.Contains(c.Id))
            .Select(c => c.SecretHash)
            .ToListAsync();
        await InvalidateAsync(hashes);
    }

    /// <summary>A hash that matches no row, thrown so HybridCache stores nothing. Never escapes <see cref="ResolveAsync"/>.</summary>
    private sealed class UnknownSecretException : Exception;
}
