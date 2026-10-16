using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;

namespace PrintLogApi.SpoolTags.Catalog;

public sealed record CatalogImportResult(int Added, int Updated, int Removed, bool NotModified);

/// <summary>
/// Syncs <see cref="FilamentCatalogEntry"/> with SpoolmanDB. Entries that disappear are marked
/// <see cref="FilamentCatalogEntry.RemovedAt"/>, never deleted: filaments and SKU votes point at them.
/// </summary>
public sealed class FilamentCatalogImporter(
    PrintLogContext context,
    SpoolmanDbClient client,
    TimeProvider timeProvider,
    ILogger<FilamentCatalogImporter> logger)
{
    private const string LockResource = "SpoolTags.CatalogImport";

    // The last ETag this process saw. Losing it (a restart) costs one full download.
    private static string? _etag;

    /// <summary>Forgets the stored ETag, so tests do not depend on order.</summary>
    internal static void ResetETag() => _etag = null;

    public async Task<CatalogImportResult> ImportAsync(CancellationToken ct)
    {
        var useLock = context.Database.IsSqlServer();
        if (useLock)
        {
            // A session lock lives on the connection, so keep it open for the whole import.
            await context.Database.OpenConnectionAsync(ct);
        }

        try
        {
            if (useLock && !await TryAcquireLockAsync(ct))
            {
                logger.LogInformation("Another instance is importing the filament catalog; skipping.");
                return new CatalogImportResult(0, 0, 0, false);
            }

            try
            {
                return await ImportCoreAsync(ct);
            }
            finally
            {
                if (useLock)
                {
                    await ReleaseLockAsync();
                }
            }
        }
        finally
        {
            if (useLock)
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task<CatalogImportResult> ImportCoreAsync(CancellationToken ct)
    {
        var fetch = await client.FetchAsync(_etag, ct);
        if (fetch.NotModified)
        {
            return new CatalogImportResult(0, 0, 0, true);
        }

        var incoming = new Dictionary<string, FilamentCatalogEntry>(StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var entry in fetch.Entries ?? [])
        {
            var mapped = Map(entry, now);
            if (mapped != null)
            {
                incoming.TryAdd(mapped.Id, mapped);
            }
        }

        // A feed with nothing usable is a broken feed, not a catalog that emptied overnight.
        if (incoming.Count == 0)
        {
            throw new CatalogFetchException("The SpoolmanDB catalog held no usable entries.");
        }

        var existing = await context.FilamentCatalogEntries.ToDictionaryAsync(e => e.Id, StringComparer.Ordinal, ct);
        int added = 0, updated = 0, removed = 0;

        foreach (var (id, fresh) in incoming)
        {
            if (!existing.TryGetValue(id, out var row))
            {
                context.FilamentCatalogEntries.Add(fresh);
                added++;
            }
            else if (row.RemovedAt != null || !SameContent(row, fresh))
            {
                Copy(fresh, row);
                row.RemovedAt = null;
                row.ImportedAt = now;
                updated++;
            }
        }

        foreach (var (id, row) in existing)
        {
            if (row.RemovedAt == null && !incoming.ContainsKey(id))
            {
                row.RemovedAt = now;
                removed++;
            }
        }

        await context.SaveChangesAsync(ct);
        _etag = fetch.ETag;
        return new CatalogImportResult(added, updated, removed, false);
    }

    private static FilamentCatalogEntry? Map(SpoolmanEntry e, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(e.Id) || e.Id.Length > 200
            || string.IsNullOrWhiteSpace(e.Manufacturer) || e.Manufacturer.Length > 100
            || string.IsNullOrWhiteSpace(e.Name) || e.Name.Length > 255
            || string.IsNullOrWhiteSpace(e.Material) || e.Material.Length > 50
            || e.Density is not { } density || e.Weight is not { } weight || e.Diameter is not { } diameter)
        {
            return null;
        }

        var hexes = e.ColorHexes?.Select(CleanHex).OfType<string>().ToList();
        var hasHexes = hexes is { Count: > 0 };
        return new FilamentCatalogEntry
        {
            Id = e.Id,
            Manufacturer = e.Manufacturer,
            Name = e.Name,
            Material = e.Material,
            Density = density,
            WeightG = (int)Math.Round(weight),
            SpoolWeightG = Round(e.SpoolWeight),
            SpoolType = Fit(e.SpoolType, 20),
            DiameterMm = diameter,
            ColorHex = hasHexes ? hexes![0] : CleanHex(e.ColorHex),
            ColorHexesJson = hasHexes ? JsonSerializer.Serialize(hexes) : null,
            ExtruderTemp = Round(e.ExtruderTemp),
            ExtruderTempMin = Bound(e.ExtruderTempRange, 0),
            ExtruderTempMax = Bound(e.ExtruderTempRange, 1),
            BedTemp = Round(e.BedTemp),
            BedTempMin = Bound(e.BedTempRange, 0),
            BedTempMax = Bound(e.BedTempRange, 1),
            Finish = Fit(e.Finish, 30),
            Pattern = Fit(e.Pattern, 30),
            Translucent = e.Translucent ?? false,
            Glow = e.Glow ?? false,
            ImportedAt = now,
        };
    }

    private static string? CleanHex(string? hex)
    {
        var trimmed = hex?.Trim().TrimStart('#');
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 8 ? null : trimmed;
    }

    private static string? Fit(string? value, int max) => string.IsNullOrWhiteSpace(value) || value.Length > max ? null : value;

    private static int? Round(double? value) => value is { } v ? (int)Math.Round(v) : null;

    private static int? Bound(double[]? range, int index) => range is { Length: 2 } ? (int)Math.Round(range[index]) : null;

    private static bool SameContent(FilamentCatalogEntry a, FilamentCatalogEntry b) =>
        a.Manufacturer == b.Manufacturer && a.Name == b.Name && a.Material == b.Material
        && a.Density == b.Density && a.WeightG == b.WeightG && a.SpoolWeightG == b.SpoolWeightG
        && a.SpoolType == b.SpoolType && a.DiameterMm == b.DiameterMm && a.ColorHex == b.ColorHex
        && a.ColorHexesJson == b.ColorHexesJson && a.ExtruderTemp == b.ExtruderTemp
        && a.ExtruderTempMin == b.ExtruderTempMin && a.ExtruderTempMax == b.ExtruderTempMax
        && a.BedTemp == b.BedTemp && a.BedTempMin == b.BedTempMin && a.BedTempMax == b.BedTempMax
        && a.Finish == b.Finish && a.Pattern == b.Pattern && a.Translucent == b.Translucent && a.Glow == b.Glow;

    private static void Copy(FilamentCatalogEntry from, FilamentCatalogEntry to)
    {
        to.Manufacturer = from.Manufacturer;
        to.Name = from.Name;
        to.Material = from.Material;
        to.Density = from.Density;
        to.WeightG = from.WeightG;
        to.SpoolWeightG = from.SpoolWeightG;
        to.SpoolType = from.SpoolType;
        to.DiameterMm = from.DiameterMm;
        to.ColorHex = from.ColorHex;
        to.ColorHexesJson = from.ColorHexesJson;
        to.ExtruderTemp = from.ExtruderTemp;
        to.ExtruderTempMin = from.ExtruderTempMin;
        to.ExtruderTempMax = from.ExtruderTempMax;
        to.BedTemp = from.BedTemp;
        to.BedTempMin = from.BedTempMin;
        to.BedTempMax = from.BedTempMax;
        to.Finish = from.Finish;
        to.Pattern = from.Pattern;
        to.Translucent = from.Translucent;
        to.Glow = from.Glow;
    }

    private async Task<bool> TryAcquireLockAsync(CancellationToken ct)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
            SELECT @result;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = LockResource;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) >= 0;
    }

    private async Task ReleaseLockAsync()
    {
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session'";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@resource";
            parameter.Value = LockResource;
            command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Closing the connection drops a session lock anyway.
            logger.LogWarning(ex, "Releasing the catalog import lock failed");
        }
    }
}
