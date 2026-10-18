using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Caching;
using PrintLogApi.Email;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Sync;
using PrintLogApi.Profiles;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Services.Sync;

/// <summary>The calendar month the account totals cover, in a canonical zone.</summary>
public sealed record SyncMonth(string Key, string ZoneId, DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>The month containing <paramref name="now"/> in <paramref name="zone"/>.</summary>
    public static SyncMonth Containing(DateTimeOffset now, SyncZone zone)
    {
        var local = EmailTimeZones.LocalDate(now, zone.Zone);
        var first = new DateOnly(local.Year, local.Month, 1);
        return new SyncMonth(
            first.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            zone.Id,
            EmailTimeZones.AtLocal(first, 0, zone.Zone).ToUniversalTime(),
            EmailTimeZones.AtLocal(first.AddMonths(1), 0, zone.Zone).ToUniversalTime());
    }
}

/// <summary>
/// The outcome of a snapshot request: the entity tag, and the snapshot unless the caller's
/// <c>If-None-Match</c> already holds that tag (then <see cref="Snapshot"/> is null).
/// </summary>
public sealed record SyncSnapshotResult(SyncSnapshot? Snapshot, string ETag);

public interface ISyncSnapshotService
{
    /// <summary>
    /// The user's snapshot as of <paramref name="now"/>. <paramref name="zone"/> null means the
    /// user's saved time zone setting, else UTC. <paramref name="notModified"/> is asked whether
    /// the caller already holds a tag; when it says yes, the result carries no snapshot and the
    /// cached core is not touched if it can be avoided.
    /// </summary>
    Task<SyncSnapshotResult> GetAsync(long userId, SyncZone? zone, DateTimeOffset now, Func<string, bool> notModified, CancellationToken ct);
}

/// <summary>
/// Builds the integration snapshot (#234). See AGENTS.md, "Integration polling". Four cache
/// entries, all keyed on the user's version GUID so any write replaces them:
/// <list type="bullet">
/// <item><b>core</b> (<see cref="CoreKey"/>, 15 min): printers and spools, with a hash of their
/// serialized form. The expensive part: three statements.</item>
/// <item><b>core hash</b> (<see cref="HashKey"/>, 1 h): just that hash, so a conditional request
/// can answer 304 without the core, even after the core expired. Written by the core factory, so
/// it can outlive the core but never describe a different one.</item>
/// <item><b>totals</b> (zone and month in the key, 1 h): two small statements.</item>
/// <item><b>saved zone</b> (1 h): the user's time zone setting; settings writes bump the version.</item>
/// </list>
/// Connection health is read live on every request (one statement), because heartbeats do not
/// bump the version and going stale is the absence of an event.
/// </summary>
public sealed class SyncSnapshotService(
    PrintLogContext context,
    HybridCache cache,
    CachedComputation computation,
    ICacheVersionService cacheVersion) : ISyncSnapshotService
{
    private static readonly HybridCacheEntryOptions CoreOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(15),
        LocalCacheExpiration = TimeSpan.FromMinutes(15),
    };

    /// <summary>
    /// The small entries. An hour bounds how long a change that missed its version bump can hide
    /// behind a 304: once the hash entry expires, the next request rebuilds the core and hashes
    /// the new content.
    /// </summary>
    private static readonly HybridCacheEntryOptions SmallOptions = new()
    {
        Expiration = TimeSpan.FromHours(1),
        LocalCacheExpiration = TimeSpan.FromHours(1),
    };

    /// <summary>Reads an entry without creating one (the ClaimsTransformer idiom).</summary>
    private static readonly HybridCacheEntryOptions PeekOptions = new()
    {
        Flags = HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite,
    };

    internal static string CoreKey(long userId, string guid) => $"sync_core:{userId}:{guid}";

    internal static string HashKey(long userId, string guid) => $"sync_core_hash:{userId}:{guid}";

    private static string TotalsKey(long userId, string guid, SyncMonth month) => $"sync_totals:{userId}:{guid}:{month.ZoneId}:{month.Key}";

    private static string ZoneKey(long userId, string guid) => $"sync_zone:{userId}:{guid}";

    public async Task<SyncSnapshotResult> GetAsync(long userId, SyncZone? zone, DateTimeOffset now, Func<string, bool> notModified, CancellationToken ct)
    {
        var guid = cacheVersion.GetUserCacheVersion(userId);
        var version = SyncValidators.Version(userId, guid);

        zone ??= await SavedZoneAsync(userId, guid, ct);
        var month = SyncMonth.Containing(now, zone);

        // CachedComputation's scope and HybridCache's token for every factory: see CachedComputation.
        var totals = await cache.GetOrCreateAsync(
            TotalsKey(userId, guid, month),
            (computation, userId, month),
            static (state, token) => state.computation.RunAsync(
                (services, innerToken) => LoadTotalsAsync(services.GetRequiredService<PrintLogContext>(), state.userId, state.month, innerToken),
                token),
            SmallOptions,
            cancellationToken: ct);

        var connections = await LoadConnectionsAsync(context, userId, now, ct);

        // The 304 path: the core's hash alone is enough to name the response.
        var knownHash = await cache.GetOrCreateAsync<string?>(
            HashKey(userId, guid), static _ => ValueTask.FromResult<string?>(null), PeekOptions, cancellationToken: ct);
        if (knownHash is not null)
        {
            var known = ETag(version, knownHash, totals, connections);
            if (notModified(known))
            {
                return new SyncSnapshotResult(null, known);
            }
        }

        var core = await cache.GetOrCreateAsync(
            CoreKey(userId, guid),
            (computation, cache, userId, guid),
            static (state, token) => state.computation.RunAsync(
                async (services, innerToken) =>
                {
                    var entry = await LoadCoreAsync(services.GetRequiredService<PrintLogContext>(), state.userId, innerToken);
                    await state.cache.SetAsync(HashKey(state.userId, state.guid), entry.Hash, SmallOptions, cancellationToken: innerToken);
                    return entry;
                },
                token),
            CoreOptions,
            cancellationToken: ct);

        var etag = ETag(version, core.Hash, totals, connections);
        if (notModified(etag))
        {
            return new SyncSnapshotResult(null, etag);
        }

        // The cached core is shared; the overlay goes onto copies.
        var printers = core.Printers
            .Select(p => connections.TryGetValue(p.Id, out var c) ? p with { Connection = c } : p)
            .ToList();
        return new SyncSnapshotResult(
            new SyncSnapshot(SyncSnapshot.CurrentSchemaVersion, version, printers, core.Spools, totals),
            etag);
    }

    /// <summary>
    /// Names the response from what it is built of: the version (it is in the body), the core's
    /// content hash, the totals and the connection overlay. Content rather than version alone, so
    /// a change that missed its version bump gets a new tag once the cached hash expires.
    /// </summary>
    private static string ETag(string version, string coreHash, SyncAccountTotals totals, Dictionary<long, SyncConnection> connections) =>
        SyncValidators.Hash(string.Join('|',
            new[]
            {
                version, coreHash, totals.TimeZone, totals.Month,
                totals.PrintCount.ToString(CultureInfo.InvariantCulture), totals.Grams.ToString("R", CultureInfo.InvariantCulture),
            }
            .Concat(connections.OrderBy(c => c.Key).Select(c => $"{c.Key}:{c.Value.Kind}:{c.Value.Status}"))));

    /// <summary>The user's saved time zone setting (the one the monthly recap email uses), canonical, else UTC.</summary>
    private async Task<SyncZone> SavedZoneAsync(long userId, string guid, CancellationToken ct)
    {
        var id = await cache.GetOrCreateAsync(
            ZoneKey(userId, guid),
            (computation, userId),
            static (state, token) => state.computation.RunAsync(
                async (services, innerToken) =>
                {
                    var saved = await services.GetRequiredService<PrintLogContext>().UserSettings
                        .AsNoTracking()
                        .Where(s => s.UserId == state.userId && s.UserSettingTypeId == EvaluationContext.TimeZoneSettingTypeId)
                        .Select(s => s.Value)
                        .FirstOrDefaultAsync(innerToken);
                    return SyncZone.TryResolve(saved, out var resolved) ? resolved.Id : SyncZone.Utc.Id;
                },
                token),
            SmallOptions,
            cancellationToken: ct);
        return SyncZone.TryResolve(id, out var zone) ? zone : SyncZone.Utc;
    }

    /// <summary>
    /// Connector health per printer: the freshest bound connection, online until
    /// <see cref="Connection.StaleAfter"/> without a heartbeat, the rule <c>GET /api/Connections</c> uses.
    /// </summary>
    internal static async Task<Dictionary<long, SyncConnection>> LoadConnectionsAsync(PrintLogContext db, long userId, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.Connections
            .AsNoTracking()
            .Where(c => c.UserId == userId && c.PrinterId != null)
            .Select(c => new { PrinterId = c.PrinterId!.Value, c.Kind, c.LastSeenAt })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.PrinterId)
            .ToDictionary(g => g.Key, g =>
            {
                var freshest = g.OrderByDescending(r => r.LastSeenAt).ThenBy(r => r.Kind, StringComparer.Ordinal).First();
                var lastSeen = new DateTimeOffset(DateTime.SpecifyKind(freshest.LastSeenAt, DateTimeKind.Utc));
                return new SyncConnection(freshest.Kind, now - lastSeen >= Connection.StaleAfter ? SyncConnection.Stale : SyncConnection.Online);
            });
    }

    /// <summary>Prints started in <paramref name="month"/> and the grams their usage rows took: two statements.</summary>
    internal static async Task<SyncAccountTotals> LoadTotalsAsync(PrintLogContext db, long userId, SyncMonth month, CancellationToken ct)
    {
        var printCount = await db.Prints
            .AsNoTracking()
            .CountAsync(p => p.CreatedById == userId && p.StartDate >= month.From && p.StartDate < month.To, ct);
        var monthMg = await db.PrintFilament
            .AsNoTracking()
            .Where(pf => pf.Print.CreatedById == userId && pf.Print.StartDate >= month.From && pf.Print.StartDate < month.To)
            .Select(FilamentProfile.UsageRowMg)
            .SumAsync(ct);
        return new SyncAccountTotals(month.Key, month.ZoneId, printCount, Grams(monthMg));
    }

    /// <summary>
    /// The expensive part of a miss: printers, loaded slots and spools, three statements however
    /// many printers and spools the user has (<c>SyncSnapshotQueryShapeTests</c>), plus a hash of
    /// the result so the response can be named by its content.
    /// </summary>
    internal static async Task<CoreEntry> LoadCoreAsync(PrintLogContext db, long userId, CancellationToken ct)
    {
        var printers = await PrintersQuery(db, userId).ToListAsync(ct);

        var loaded = await db.PrinterFilament
            .AsNoTracking()
            .Where(pf => pf.UnloadedDateTime == null && pf.Printer.UserId == userId && pf.Filament.CreatedById == userId)
            .Select(pf => new { pf.Id, pf.PrinterId, pf.FilamentId, pf.Slot, pf.SlotLabel, pf.LoadedDateTime })
            .ToListAsync(ct);

        var spools = (await SpoolsQuery(db, userId).ToListAsync(ct))
            .Select(ToSpool)
            .OrderBy(s => s.SpoolNumber == null)
            .ThenBy(s => s.SpoolNumber)
            .ThenBy(s => s.Id)
            .ToList();
        var spoolsById = spools.ToDictionary(s => s.Id);

        var loadedByPrinter = loaded
            .Where(l => spoolsById.ContainsKey(l.FilamentId))
            .GroupBy(l => l.PrinterId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SyncLoadedSpool>)g
                .OrderBy(l => l.Slot == null)
                .ThenBy(l => l.Slot)
                .ThenByDescending(l => l.LoadedDateTime)
                .ThenBy(l => l.Id)
                .Select(l =>
                {
                    var s = spoolsById[l.FilamentId];
                    return new SyncLoadedSpool(l.Slot, l.SlotLabel, s.Id, s.SpoolNumber, s.Name, s.Material, s.ColorHex, s.Colors, s.RemainingGrams);
                })
                .ToList());

        var syncPrinters = printers
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id)
            .Select(p => new SyncPrinter(
                p.Id, p.Name, p.Make, p.Model, p.IsActive, p.SlotCount,
                Connection: null,
                CurrentPrint: p.Current is { } c
                    ? new SyncCurrentPrint(c.Id, c.Title, c.StartDate, c.EstimatedSeconds,
                        c.StartDate.HasValue && c.EstimatedSeconds.HasValue ? c.StartDate.Value.AddSeconds(c.EstimatedSeconds.Value) : null)
                    : null,
                LastFinishedPrint: p.LastFinished is { } f
                    ? new SyncFinishedPrint(f.Id, f.Title, StatusName(f.Status), f.StartDate,
                        f.StartDate.HasValue && f.PrintSeconds.HasValue ? f.StartDate.Value.AddSeconds(f.PrintSeconds.Value) : null)
                    : null,
                Loaded: loadedByPrinter.TryGetValue(p.Id, out var l) ? l : [],
                PrintTimeSeconds: p.PrintSeconds,
                LastMaintenance: p.LastMaintenance is { } m ? new SyncMaintenance(m.Id, m.Date, m.Category, m.Description) : null,
                OpenMaintenance: new SyncOpenMaintenance(p.OpenMaintenanceCount, p.NextMaintenanceDate)))
            .ToList();

        // The body is a pure function of these two lists (ordered above), so their bytes name it.
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { syncPrinters, spools })))[..32];
        return new CoreEntry(syncPrinters, spools, hash);
    }

    /// <summary>
    /// The cached core. <see cref="ImmutableObjectAttribute"/> lets HybridCache share the stored
    /// instance instead of deserializing a copy per hit; treat it as read-only.
    /// </summary>
    [ImmutableObject(true)]
    internal sealed record CoreEntry(IReadOnlyList<SyncPrinter> Printers, IReadOnlyList<SyncSpool> Spools, string Hash);

    /// <summary>
    /// The user's printers with their newest Printing print and newest finished print, all-time print
    /// seconds, and last/open maintenance, as correlated subqueries in one statement. Exposed for the translation tests.
    /// </summary>
    internal static IQueryable<PrinterRow> PrintersQuery(PrintLogContext db, long userId) =>
        db.Printers
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new PrinterRow
            {
                Id = p.Id,
                Name = p.Name,
                Make = p.Make,
                Model = p.Model,
                IsActive = p.IsActive,
                SlotCount = p.SlotCount,
                // The analytics duration rule (PrinterAnalyticsService): recorded time, else estimate, else 0.
                PrintSeconds = db.Prints
                    .Where(pr => pr.PrinterId == p.Id && pr.CreatedById == userId)
                    .Sum(pr =>
                        (long)(pr.PrintTimeInSeconds.HasValue && pr.PrintTimeInSeconds > 0 ? pr.PrintTimeInSeconds.Value
                        : pr.EstimatedPrintTimeInSeconds.HasValue && pr.EstimatedPrintTimeInSeconds > 0 ? pr.EstimatedPrintTimeInSeconds.Value
                        : 0)),
                LastMaintenance = db.PrinterMaintenance
                    .Where(m => m.PrinterId == p.Id && m.Done)
                    .OrderByDescending(m => m.Date)
                    .ThenByDescending(m => m.Id)
                    .Select(m => new MaintenanceRow { Id = m.Id, Date = m.Date, Category = m.Category, Description = m.Description })
                    .FirstOrDefault(),
                OpenMaintenanceCount = db.PrinterMaintenance.Count(m => m.PrinterId == p.Id && !m.Done),
                // Top-1 rather than Min: SQLite cannot aggregate a DateTimeOffset.
                NextMaintenanceDate = db.PrinterMaintenance
                    .Where(m => m.PrinterId == p.Id && !m.Done)
                    .OrderBy(m => m.Date)
                    .Select(m => (DateTimeOffset?)m.Date)
                    .FirstOrDefault(),
                Current = db.Prints
                    .Where(pr => pr.PrinterId == p.Id && pr.CreatedById == userId && pr.Status == PrintStatus.Printing)
                    .OrderByDescending(pr => pr.StartDate)
                    .ThenByDescending(pr => pr.Id)
                    .Select(pr => new PrintRow
                    {
                        Id = pr.Id,
                        Title = pr.Title,
                        Status = pr.Status,
                        StartDate = pr.StartDate,
                        EstimatedSeconds = pr.EstimatedPrintTimeInSeconds,
                        PrintSeconds = pr.PrintTimeInSeconds,
                    })
                    .FirstOrDefault(),
                LastFinished = db.Prints
                    .Where(pr => pr.PrinterId == p.Id && pr.CreatedById == userId
                        && (pr.Status == PrintStatus.Success || pr.Status == PrintStatus.Failed
                            || pr.Status == PrintStatus.Cancelled || pr.Status == PrintStatus.PartialSuccess))
                    .OrderByDescending(pr => pr.StartDate)
                    .ThenByDescending(pr => pr.Id)
                    .Select(pr => new PrintRow
                    {
                        Id = pr.Id,
                        Title = pr.Title,
                        Status = pr.Status,
                        StartDate = pr.StartDate,
                        EstimatedSeconds = pr.EstimatedPrintTimeInSeconds,
                        PrintSeconds = pr.PrintTimeInSeconds,
                    })
                    .FirstOrDefault(),
            });

    /// <summary>
    /// Every material the user owns, with the shared remaining and consumed figures composed into
    /// the projection, so the SQL computes them exactly as the web app and the Spoolman API do.
    /// Exposed for the translation tests (the correlated sums are the SQL Server 8124 shape).
    /// </summary>
    internal static IQueryable<SpoolRow> SpoolsQuery(PrintLogContext db, long userId) =>
        db.Filaments
            .AsNoTracking()
            .Where(f => f.CreatedById == userId)
            .Select(SpoolProjection);

    private static readonly System.Linq.Expressions.Expression<Func<Filament, SpoolRow>> SpoolProjection =
        ExpressionSplice.With<Filament, long?, long, SpoolRow>(
            (f, remainingMg, consumedMg) => new SpoolRow
            {
                Id = f.Id,
                SpoolNumber = f.SpoolNumber,
                Name = f.DisplayName,
                Brand = f.Brand,
                Material = f.MaterialType,
                Category = f.MaterialCategoryNickname,
                ColorName = f.ColorName,
                ColorHex = f.ColorHex,
                Colors = f.Colors,
                StorageLocation = f.StorageLocation,
                IsActive = f.IsActive,
                InitialMg = f.InitialNominalWeightMg,
                RemainingMg = remainingMg,
                ConsumedMg = consumedMg,
            },
            FilamentProfile.RemainingMg,
            FilamentProfile.ConsumedMg);

    private static SyncSpool ToSpool(SpoolRow r)
    {
        IEnumerable<string?> candidates = r.Colors is { Count: > 0 } list ? list : new[] { r.ColorHex };
        var colors = candidates
            .Select(NormalizeHex)
            .OfType<string>()
            .ToList();

        return new SyncSpool(
            r.Id, r.SpoolNumber, r.Name, r.Brand, r.Material, r.Category, r.ColorName,
            colors.FirstOrDefault(), colors,
            // Untracked (no initial weight) is null, never a negative balance; see #230.
            RemainingGrams: r.RemainingMg.HasValue ? Grams(Math.Max(0, r.RemainingMg.Value)) : null,
            UsedGrams: Grams(Math.Max(0, r.ConsumedMg)),
            InitialGrams: r.InitialMg.HasValue ? Grams(r.InitialMg.Value) : null,
            r.StorageLocation, r.IsActive);
    }

    /// <summary><c>#RRGGBB</c> or <c>#RRGGBBAA</c>, upper case; anything else is dropped.</summary>
    internal static string? NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var hex = value.Trim().TrimStart('#').ToUpperInvariant();
        return hex.Length is 6 or 8 && hex.All(Uri.IsHexDigit) ? "#" + hex : null;
    }

    /// <summary>Milligrams to grams, to 0.1 g, so equal data serializes to equal bytes.</summary>
    private static double Grams(double milligrams) => Math.Round(milligrams / 1000.0, 1, MidpointRounding.AwayFromZero);

    private static string StatusName(PrintStatus status) => status switch
    {
        PrintStatus.Success => "success",
        PrintStatus.Failed => "failed",
        PrintStatus.Cancelled => "canceled",
        PrintStatus.PartialSuccess => "partial_success",
        _ => throw new InvalidOperationException($"{status} is not a finished status."),
    };

    internal sealed class PrinterRow
    {
        public long Id { get; init; }
        public string? Name { get; init; }
        public string? Make { get; init; }
        public string? Model { get; init; }
        public bool IsActive { get; init; }
        public int SlotCount { get; init; }
        public PrintRow? Current { get; init; }
        public PrintRow? LastFinished { get; init; }
        public long PrintSeconds { get; init; }
        public MaintenanceRow? LastMaintenance { get; init; }
        public int OpenMaintenanceCount { get; init; }
        public DateTimeOffset? NextMaintenanceDate { get; init; }
    }

    internal sealed class MaintenanceRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset Date { get; init; }
        public string? Category { get; init; }
        public string? Description { get; init; }
    }

    internal sealed class PrintRow
    {
        public long Id { get; init; }
        public string? Title { get; init; }
        public PrintStatus Status { get; init; }
        public DateTimeOffset? StartDate { get; init; }
        public int? EstimatedSeconds { get; init; }
        public int? PrintSeconds { get; init; }
    }

    internal sealed class SpoolRow
    {
        public Guid Id { get; init; }
        public int? SpoolNumber { get; init; }
        public string? Name { get; init; }
        public string? Brand { get; init; }
        public string? Material { get; init; }
        public string Category { get; init; } = "";
        public string? ColorName { get; init; }
        public string? ColorHex { get; init; }
        public List<string>? Colors { get; init; }
        public string? StorageLocation { get; init; }
        public bool IsActive { get; init; }
        public long? InitialMg { get; init; }
        public long? RemainingMg { get; init; }
        public long ConsumedMg { get; init; }
    }
}
