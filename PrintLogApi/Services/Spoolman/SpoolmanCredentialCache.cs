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
/// secret hash, <b>negative results included</b>.
///
/// <para>Negative caching is the point. Moonraker retries its websocket every 2 seconds with no
/// backoff, so a printer whose connection was revoked, paused by a downgrade, or mistyped would
/// otherwise cost a query 30 times a minute, indefinitely. Unlike the API-key lookup, which
/// throws to keep an unknown key out of the cache, an unknown secret is safe to cache: secrets
/// are 256 random bits minted server-side, so an unknown hash cannot later become valid.</para>
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
        // The context comes from CachedComputation's scope, and the token is HybridCache's: the
        // result is shared with every concurrent caller on this hash. See CachedComputation.
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
                    return SpoolmanCredential.Unknown;
                }

                var status = row.RevokedAt.HasValue
                    ? SpoolmanCredentialStatus.Revoked
                    : row.DisabledAt.HasValue ? SpoolmanCredentialStatus.Disabled : SpoolmanCredentialStatus.Valid;
                return new SpoolmanCredential(status, row.Id, row.UserId, row.PrinterId);
            }, token),
            Options,
            cancellationToken: ct);
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
}
