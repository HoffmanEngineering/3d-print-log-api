using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Services.AgentReleases;

public interface IAgentReleaseCatalog
{
    /// <summary>
    /// The newest release of the agent behind connections of <paramref name="kind"/>, as
    /// <c>X.Y.Z</c>, or null when no agent is known for the kind or it has no release yet. Never
    /// throws for a fetch failure: a failed reload keeps the version found earlier.
    /// </summary>
    Task<string?> GetLatestVersionAsync(string kind, CancellationToken cancellationToken);
}

/// <summary>
/// Reads each agent's latest release from its GitHub repository's tags, so the agent hears about
/// an update in its connection check-in rather than by calling GitHub itself
/// (printlog-bridge#39). The bridge publishes a Docker image for every <c>vX.Y.Z</c> tag and makes
/// no GitHub release, so tags are the source rather than <c>releases/latest</c>.
///
/// <para>The versions are held in a field rather than in HybridCache, for the reason
/// <c>DocsCatalog</c> gives: a handful of values shared by every caller, not compute-on-miss over
/// the database, and a single gate gives the same stampede protection.</para>
/// </summary>
public sealed partial class AgentReleaseCatalog(
    IHttpClientFactory httpClientFactory,
    IOptions<AgentReleaseOptions> options,
    TimeProvider timeProvider,
    ILogger<AgentReleaseCatalog> logger) : IAgentReleaseCatalog, IDisposable
{
    public const string HttpClientName = "AgentReleases";

    /// <summary>Own timeout, not the caller's token: one heartbeat disconnecting must not fail the read others wait on.</summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(5);

    private const int MaxTagsBytes = 256 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Snapshot> _byRepository = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Snapshot(string? Version, DateTimeOffset ExpiresAt);

    public async Task<string?> GetLatestVersionAsync(string kind, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Repositories.TryGetValue(kind, out var repository) || string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        if (Fresh(repository) is { } fresh)
        {
            return fresh.Version;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh(repository) is { } current)
            {
                return current.Version;
            }

            var loaded = await LoadAsync(repository);
            var previous = _byRepository.GetValueOrDefault(repository);
            var now = timeProvider.GetUtcNow();
            var next = loaded is { } version
                ? new Snapshot(version, now.AddMinutes(settings.CacheMinutes))
                // A failure keeps the version found earlier: telling an agent nothing for a while
                // is fine, telling it nothing because GitHub had a bad minute is not needed.
                : new Snapshot(previous?.Version, now.AddMinutes(settings.FailureCacheMinutes));
            _byRepository[repository] = next;
            return next.Version;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private Snapshot? Fresh(string repository)
        => _byRepository.TryGetValue(repository, out var snapshot) && timeProvider.GetUtcNow() < snapshot.ExpiresAt
            ? snapshot
            : null;

    /// <summary>The highest <c>vX.Y.Z</c> tag, or null when the read failed or found none.</summary>
    private async Task<string?> LoadAsync(string repository)
    {
        using var timeout = new CancellationTokenSource(LoadTimeout);
        var client = httpClientFactory.CreateClient(HttpClientName);
        var url = $"https://api.github.com/repos/{repository}/tags?per_page=100";
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Reading the releases of {Repository} returned {StatusCode}.", repository, (int)response.StatusCode);
                return null;
            }
            if (response.Content.Headers.ContentLength > MaxTagsBytes)
            {
                logger.LogWarning("The tag list of {Repository} is over {MaxBytes} bytes.", repository, MaxTagsBytes);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (body.Length > MaxTagsBytes)
            {
                logger.LogWarning("The tag list of {Repository} is over {MaxBytes} bytes.", repository, MaxTagsBytes);
                return null;
            }
            var latest = Highest(body);
            if (latest is null)
            {
                logger.LogInformation("{Repository} has no vX.Y.Z tag yet.", repository);
            }
            return latest;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            logger.LogWarning(ex, "Reading the releases of {Repository} failed.", repository);
            return null;
        }
    }

    private static string? Highest(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        Version? highest = null;
        foreach (var tag in document.RootElement.EnumerateArray())
        {
            if (tag.ValueKind == JsonValueKind.Object
                && tag.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && ReleaseTag().Match(name.GetString()!) is { Success: true } match
                && AgentVersion.Parse(match.Groups[1].Value) is { } version
                && (highest is null || version > highest))
            {
                highest = version;
            }
        }
        return highest?.ToString(3);
    }

    /// <summary>A release tag: <c>v</c> and three numbers, nothing after (so no prereleases).</summary>
    [GeneratedRegex(@"^v(\d+\.\d+\.\d+)$")]
    private static partial Regex ReleaseTag();
}
