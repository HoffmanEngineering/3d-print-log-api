using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Caching;
using PrintLogApi.Email;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Sync;
using PrintLogApi.Profiles;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Services.Sync;

/// <summary>The calendar month the account totals cover, in the caller's zone.</summary>
public sealed record SyncMonth(string Key, string ZoneId, DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>The month containing <paramref name="now"/> in <paramref name="zone"/>; <paramref name="zoneId"/> is echoed as given.</summary>
    public static SyncMonth Containing(DateTimeOffset now, TimeZoneInfo zone, string zoneId)
    {
        var local = EmailTimeZones.LocalDate(now, zone);
        var first = new DateOnly(local.Year, local.Month, 1);
        return new SyncMonth(
            first.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            zoneId,
            EmailTimeZones.AtLocal(first, 0, zone).ToUniversalTime(),
            EmailTimeZones.AtLocal(first.AddMonths(1), 0, zone).ToUniversalTime());
    }
}

/// <summary>A snapshot and the entity tag that names its content.</summary>
public sealed record SyncSnapshotResult(SyncSnapshot Snapshot, string ETag);

public interface ISyncSnapshotService
{
    /// <summary>The user's snapshot as of <paramref name="now"/>, for the month <paramref name="month"/>.</summary>
    Task<SyncSnapshotResult> GetAsync(long userId, SyncMonth month, DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// Builds the integration snapshot (#234). Everything but connection health is computed on a miss
/// and cached in HybridCache on the user's version GUID, zone and month; connection health is read
/// live on every request, because heartbeats do not bump the version and going stale is the
/// absence of an event. See AGENTS.md, "Integration polling".
/// </summary>
public sealed class SyncSnapshotService(
    PrintLogContext context,
    HybridCache cache,
    CachedComputation computation,
    ICacheVersionService cacheVersion) : ISyncSnapshotService
{
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(15),
        LocalCacheExpiration = TimeSpan.FromMinutes(15),
    };

    public async Task<SyncSnapshotResult> GetAsync(long userId, SyncMonth month, DateTimeOffset now, CancellationToken ct)
    {
        var guid = cacheVersion.GetUserCacheVersion(userId);
        var version = SyncValidators.Version(userId, guid);

        // The month is in the key so the totals roll over at local midnight on the 1st without a
        // write. CachedComputation's scope and HybridCache's token: see CachedComputation.
        var core = await cache.GetOrCreateAsync(
            $"sync_snapshot:{userId}:{guid}:{month.ZoneId}:{month.Key}",
            (computation, userId, version, month),
            static (state, token) => state.computation.RunAsync(
                (services, innerToken) => LoadAsync(services.GetRequiredService<PrintLogContext>(), state.userId, state.version, state.month, innerToken),
                token),
            CacheOptions,
            cancellationToken: ct);

        var connections = await LoadConnectionsAsync(context, userId, now, ct);

        // The cached instance is shared; the overlay goes onto copies.
        var printers = core.Printers
            .Select(p => connections.TryGetValue(p.Id, out var c) ? p with { Connection = c } : p)
            .ToList();
        var snapshot = core with { Printers = printers };

        var etag = SyncValidators.Hash(string.Join('|',
            new[] { version, month.ZoneId, month.Key }
                .Concat(printers.Select(p => $"{p.Id}:{p.Connection?.Kind}:{p.Connection?.Status}"))));
        return new SyncSnapshotResult(snapshot, etag);
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

    /// <summary>
    /// The cache-miss path: five statements, however many printers and spools the user has
    /// (<c>SyncSnapshotQueryShapeTests</c>). The connection overlay is not part of it.
    /// </summary>
    internal static async Task<SyncSnapshot> LoadAsync(PrintLogContext db, long userId, string version, SyncMonth month, CancellationToken ct)
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

        var printCount = await db.Prints
            .AsNoTracking()
            .CountAsync(p => p.CreatedById == userId && p.StartDate >= month.From && p.StartDate < month.To, ct);
        var monthMg = await db.PrintFilament
            .AsNoTracking()
            .Where(pf => pf.Print.CreatedById == userId && pf.Print.StartDate >= month.From && pf.Print.StartDate < month.To)
            .Select(FilamentProfile.UsageRowMg)
            .SumAsync(ct);

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
                Loaded: loadedByPrinter.TryGetValue(p.Id, out var l) ? l : []))
            .ToList();

        return new SyncSnapshot(
            SyncSnapshot.CurrentSchemaVersion,
            version,
            syncPrinters,
            spools,
            new SyncAccountTotals(month.Key, month.ZoneId, printCount, Grams(monthMg)));
    }

    /// <summary>
    /// The user's printers with their newest Printing print and newest finished print, as two
    /// correlated top-1 subqueries in one statement. Exposed for the translation tests.
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
