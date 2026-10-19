using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.PrinterCatalog.Generation;

namespace PrintLogApi.PrinterCatalog;

/// <summary>
/// What one seeding pass did. <see cref="Unchanged"/> counts the file's models that needed no write;
/// a pass skipped because the version matched reports every model as unchanged.
/// <see cref="Repointed"/> counts printers moved off a merged id.
/// </summary>
public sealed record SeedResult(int Inserted, int Updated, int Retired, int Unchanged, int Repointed);

/// <summary>
/// Runs once at startup, in every environment (spec §6.2). When the embedded
/// <c>printer-catalog.json</c>'s version differs from the one recorded in
/// <see cref="PrinterCatalogVersion"/>, it upserts every model, retires the rows the file no longer
/// lists, and moves printers off merged ids. Then it builds the search index from the table,
/// whether or not anything was seeded. A failure is logged and leaves the table as it was; the API
/// starts regardless.
/// </summary>
public sealed class PrinterCatalogSeeder(
    IServiceScopeFactory scopeFactory,
    IPrinterCatalogIndex index,
    PrinterCatalogBackfill backfill,
    PrinterPopularityJob popularity,
    TimeProvider timeProvider,
    IHostApplicationLifetime lifetime,
    ILogger<PrinterCatalogSeeder> logger) : BackgroundService
{
    /// <summary>Compact, camelCase and unescaped, so "MINI+" stays readable in the column.</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The seam for tests. Read once per run; the parsed file is dropped when the run ends.</summary>
    internal Func<CatalogFile> LoadCatalog { get; init; } = PrinterCatalogFileLoader.LoadEmbedded;

    /// <summary>
    /// Waits for ApplicationStarted first: in Development and E2ETesting, <c>Startup.Configure</c>
    /// migrates (and in E2ETesting recreates) the database, and a seed racing that would fail or
    /// write into a database about to be dropped. Startup itself is never blocked.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        {
            try
            {
                await started.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
        await RunOnceAsync(stoppingToken);
    }

    /// <summary>Seeds when the version changed, then rebuilds the index. Never throws but on shutdown.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var changed = false;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var result = await SeedAsync(db, LoadCatalog(), timeProvider.GetUtcNow(), ct, logger);
            changed = result.Inserted + result.Updated + result.Retired + result.Repointed > 0;
            logger.LogInformation(
                "Printer catalog seed: {Inserted} inserted, {Updated} updated, {Retired} retired, {Unchanged} unchanged, {Repointed} printers moved off merged ids",
                result.Inserted, result.Updated, result.Retired, result.Unchanged, result.Repointed);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Printer catalog seeding failed; the catalog table was left as it was");
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            index.Rebuild(await db.PrinterModels.AsNoTracking().ToListAsync(ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Building the printer catalog index failed; catalog search is empty until the next rebuild");
        }
        catch (OperationCanceledException)
        {
        }

        if (changed)
        {
            await AfterSeedAsync(ct);
        }
    }

    /// <summary>
    /// After a seed that changed something: links printers the new models now match, then recounts
    /// owners (which rebuilds the index). Each step is best effort and logged; neither fails startup.
    /// </summary>
    internal async Task AfterSeedAsync(CancellationToken ct)
    {
        try
        {
            var result = await backfill.RunOnceAsync(ct);
            logger.LogInformation(
                "Printer catalog backfill: {Linked} linked, {Ambiguous} ambiguous, {Unmatched} unmatched",
                result.Linked, result.Ambiguous, result.Unmatched);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Printer catalog backfill failed; printers stay as they were");
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await popularity.RunOnceAsync(timeProvider.GetUtcNow(), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Printer popularity after the seed failed; owner counts stay as they were");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Brings <see cref="PrintLogContext.PrinterModels"/> in line with <paramref name="file"/>, in one
    /// transaction, and records its version. Idempotent: a second call with the same version writes
    /// nothing. Rows are never deleted and <see cref="PrinterModel.Hidden"/> and
    /// <see cref="PrinterModel.OwnerCount"/> are never written.
    /// </summary>
    public static async Task<SeedResult> SeedAsync(
        PrintLogContext db, CatalogFile file, DateTimeOffset now, CancellationToken ct, ILogger? logger = null)
    {
        var duplicate = file.Models.GroupBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new InvalidDataException($"The printer catalog lists the id {duplicate.Key} more than once.");
        }

        var skipped = new SeedResult(0, 0, 0, file.Models.Count, 0);
        if (IsSeeded(await ReadMarkerAsync(db, ct), file, logger))
        {
            return skipped;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A retry starts over from what the database holds, not from a half-applied tracker.
            db.ChangeTracker.Clear();
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                if (db.Database.IsSqlServer())
                {
                    // Two instances starting together seed one after the other. The lock is
                    // released with the transaction, and the loser then finds the winner's marker.
                    await db.Database.ExecuteSqlRawAsync(
                        """
                        DECLARE @result int;
                        EXEC @result = sp_getapplock @Resource = 'PrinterCatalogSeed', @LockMode = 'Exclusive', @LockOwner = 'Transaction';
                        IF @result < 0 THROW 50000, 'Could not take the printer catalog seed lock.', 1;
                        """,
                        ct);
                    if (IsSeeded(await ReadMarkerAsync(db, ct), file, logger))
                    {
                        await transaction.CommitAsync(ct);
                        return skipped;
                    }
                }
                var result = await ApplyAsync(db, file, now, logger, ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch
            {
                db.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private static Task<PrinterCatalogVersion?> ReadMarkerAsync(PrintLogContext db, CancellationToken ct) =>
        db.PrinterCatalogVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == PrinterCatalogVersion.SingletonId, ct);

    /// <summary>
    /// True when <paramref name="file"/> is already seeded, or is older than the file that was: a
    /// rollback deploy carries last month's catalog and must not undo the newer seed.
    /// </summary>
    private static bool IsSeeded(PrinterCatalogVersion? marker, CatalogFile file, ILogger? logger)
    {
        if (marker == null)
        {
            return false;
        }
        if (marker.Version == file.Version)
        {
            return true;
        }
        if (marker.CatalogGeneratedAt is { } seeded && file.GeneratedAt < seeded)
        {
            logger?.LogWarning(
                "The embedded printer catalog ({Version}, generated {GeneratedAt:O}) is older than the seeded one ({SeededVersion}, generated {SeededGeneratedAt:O}); not seeding it",
                file.Version, file.GeneratedAt, marker.Version, seeded);
            return true;
        }
        return false;
    }

    private static async Task<SeedResult> ApplyAsync(
        PrintLogContext db, CatalogFile file, DateTimeOffset now, ILogger? logger, CancellationToken ct)
    {
        var rows = await db.PrinterModels.ToDictionaryAsync(m => m.Id, StringComparer.Ordinal, ct);
        int inserted = 0, updated = 0, retired = 0, unchanged = 0;

        foreach (var model in file.Models)
        {
            if (!rows.TryGetValue(model.Id, out var row))
            {
                db.PrinterModels.Add(ToEntity(model, now));
                inserted++;
            }
            else if (CopyFacts(model, row))
            {
                row.UpdatedAt = now;
                updated++;
            }
            else
            {
                unchanged++;
            }
        }

        var listed = file.Models.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows.Values.Where(r => !listed.Contains(r.Id) && !r.Retired))
        {
            row.Retired = true;
            row.UpdatedAt = now;
            retired++;
        }

        // Saved before re-pointing, so a successor inserted in this pass exists for the foreign key.
        await db.SaveChangesAsync(ct);
        var repointed = await RepointMergedAsync(db, file, listed, logger, ct);

        var marker = await db.PrinterCatalogVersions.SingleOrDefaultAsync(v => v.Id == PrinterCatalogVersion.SingletonId, ct);
        if (marker == null)
        {
            db.PrinterCatalogVersions.Add(new PrinterCatalogVersion { Version = file.Version, CatalogGeneratedAt = file.GeneratedAt, SeededAt = now });
        }
        else
        {
            marker.Version = file.Version;
            marker.CatalogGeneratedAt = file.GeneratedAt;
            marker.SeededAt = now;
        }
        await db.SaveChangesAsync(ct);

        return new SeedResult(inserted, updated, retired, unchanged, repointed);
    }

    /// <summary>
    /// Moves printers linked to a merged id onto the end of its <see cref="ReservedId.MergedInto"/>
    /// chain. A cycle, or a chain ending at an id that is not a current model, leaves the links alone.
    /// </summary>
    private static async Task<int> RepointMergedAsync(
        PrintLogContext db, CatalogFile file, HashSet<string> current, ILogger? logger, CancellationToken ct)
    {
        var reserved = file.Reserved
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var moved = 0;

        foreach (var merged in file.Reserved.Where(r => r.MergedInto != null && !current.Contains(r.Id)))
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { merged.Id };
            var successor = merged.MergedInto!;
            var cycle = false;
            while (reserved.TryGetValue(successor, out var next) && next.MergedInto != null)
            {
                if (!visited.Add(successor))
                {
                    cycle = true;
                    break;
                }
                successor = next.MergedInto;
            }

            if (cycle)
            {
                logger?.LogWarning("The printer catalog's mergedInto chain from {Id} loops; its printers keep their link", merged.Id);
                continue;
            }
            if (!current.Contains(successor))
            {
                continue;
            }

            var id = merged.Id;
            moved += await db.Printers
                .Where(p => p.CatalogModelId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CatalogModelId, successor), ct);
        }

        return moved;
    }

    /// <summary>A new row for <paramref name="model"/>: visible, not retired, no owners yet.</summary>
    internal static PrinterModel ToEntity(CatalogModel model, DateTimeOffset now)
    {
        var row = new PrinterModel { Id = model.Id, UpdatedAt = now };
        CopyFacts(model, row);
        return row;
    }

    /// <summary>Copies the file's facts onto <paramref name="row"/> and un-retires it. True when anything changed.</summary>
    private static bool CopyFacts(CatalogModel model, PrinterModel row)
    {
        var changed = false;
        Set(row.Make, model.Make, v => row.Make = v);
        Set(row.Model, model.Model, v => row.Model = v);
        Set(row.AliasesJson, JsonSerializer.Serialize(model.Aliases ?? [], Json), v => row.AliasesJson = v);
        Set(row.Technology, model.Technology, v => row.Technology = v);
        Set(row.BedWidthMm, model.BedWidthMm, v => row.BedWidthMm = v);
        Set(row.BedDepthMm, model.BedDepthMm, v => row.BedDepthMm = v);
        Set(row.BedHeightMm, model.BedHeightMm, v => row.BedHeightMm = v);
        Set(row.NozzleOptionsJson, JsonSerializer.Serialize(model.NozzleOptionsMm ?? [], Json), v => row.NozzleOptionsJson = v);
        Set(row.DefaultNozzleMm, model.DefaultNozzleMm, v => row.DefaultNozzleMm = v);
        Set(row.FilamentDiameterMm, model.FilamentDiameterMm, v => row.FilamentDiameterMm = v);
        Set(row.HasHeatedBed, model.HasHeatedBed, v => row.HasHeatedBed = v);
        Set(row.HasHeatedChamber, model.HasHeatedChamber, v => row.HasHeatedChamber = v);
        Set(row.ExtruderCount, model.ExtruderCount, v => row.ExtruderCount = v);
        Set(row.DefaultSlotCount, model.DefaultSlotCount, v => row.DefaultSlotCount = v);
        Set(row.Kinematics, model.Kinematics, v => row.Kinematics = v);
        Set(row.SourcesJson, JsonSerializer.Serialize(model.Sources ?? new Dictionary<string, string>(), Json), v => row.SourcesJson = v);
        Set(row.Featured, model.Featured, v => row.Featured = v);
        Set(row.Retired, false, v => row.Retired = v);
        return changed;

        void Set<T>(T current, T value, Action<T> assign)
        {
            if (!EqualityComparer<T>.Default.Equals(current, value))
            {
                assign(value);
                changed = true;
            }
        }
    }
}
