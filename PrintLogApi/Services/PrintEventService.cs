using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Services;

public class PrintEventService(
    PrintLogContext context,
    IPrintService printService,
    IPrintFinishNotifier printFinishNotifier,
    IPrintStartNotifier printStartNotifier,
    IBlobStorageService blobStorageService,
    IImageProcessingService imageProcessing,
    IProcessedImageWriter imageWriter,
    ICacheVersionService cacheVersionService,
    TimeProvider clock,
    ILogger<PrintEventService> logger) : IPrintEventService
{
    private const string printImageContainerName = BlobContainers.PrintImages;

    public async Task<PrintStartedResult> Started(PrintStartedEvent started)
    {
        if (await FindByExternalId(started) is { } existing)
        {
            // A redelivered start. The first delivery already created the print and attached its
            // snapshot; doing either again is the duplicate this pipeline exists to prevent.
            return new PrintStartedResult(existing, WasReplayed: true);
        }

        var printer = await context.Printers
            .Where(p => p.Id == started.PrinterId)
            .Include(p => p.LoadedFilaments)
            .FirstOrDefaultAsync();

        // Null-forgiven: an unknown printer has always failed here as a 500 rather than a clean
        // error, and the integrations' tests pin that. Turning it into a not-found is tracked in #57.
        if (started.UserId != printer!.UserId)
        {
            throw new UserCannotAccessPrinterException();
        }

        var print = new Print
        {
            Status = PrintStatus.Printing,
            Source = started.Source,
            CreatedById = started.UserId,
            UpdatedById = started.UserId,
            Printer = printer,
            Title = started.Title,
            FileName = started.FileName,
            FileHash = started.FileHash,
            StartDate = started.StartDate,
            EstimatedPrintTimeInSeconds = started.EstimatedPrintTimeInSeconds,
            ExternalSource = started.ExternalSource,
            ExternalId = started.ExternalId,
            AllowComments = await DefaultAllowComments(started.UserId),
            ViewStatus = await DefaultViewStatus(started.UserId),
            FilamentUsage = new List<PrintFilament>(),
        };

        var loaded = printer.LoadedFilaments ?? new List<PrinterFilament>();
        foreach (var usage in started.Usage)
        {
            print.FilamentUsage.Add(new PrintFilament
            {
                EstimatedSource = usage.EstimatedSource,
                Id = Guid.Empty,
                FilamentId = SpoolInSlot(loaded, usage.Slot),
                Slot = usage.Slot,
                EstimatedLengthInM = usage.EstimatedLengthInM,
                Source = usage.Source,
                LengthInM = usage.LengthInM,
                Notes = usage.Notes,
            });
        }
        if (print.FilamentUsage.Count > 0)
        {
            await printService.UpdateFilamentUsageWeights(print);
        }

        context.Prints.Add(print);

        StoredSnapshot? snapshot = null;
        if (started.Snapshot is not null && await printService.GetMaxImagesPerPrint(started.UserId) > 0)
        {
            // A new print has no images yet, so this one is first and the default.
            snapshot = await AddSnapshot(print, started.Snapshot, started.UserId, displayOrder: 0);
        }

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A racing delivery of the same start won IX_Prints_User_ExternalSource_ExternalId.
            context.ChangeTracker.Clear();
            if (snapshot is not null)
            {
                await DeleteOrphanedSnapshot(snapshot);
            }
            if (await FindByExternalId(started) is { } winner)
            {
                return new PrintStartedResult(winner, WasReplayed: true);
            }
            throw;
        }

        // Saving straight through the context skips the invalidation the controllers do, so the
        // cached print summary and analytics would keep serving pre-print figures.
        cacheVersionService.InvalidateUserCache(started.UserId);
        printStartNotifier.NotifyIfJustStarted(print, previousStatus: null);
        return new PrintStartedResult(print, WasReplayed: false);
    }

    public Task<Print?> Finished(PrintFinishedEvent finished) => Finished(finished, retryIfRaced: true);

    private async Task<Print?> Finished(PrintFinishedEvent finished, bool retryIfRaced)
    {
        var query = context.Prints
            .Where(p => p.CreatedById == finished.UserId && p.Status == PrintStatus.Printing);

        if (finished.FileHash is { } hash)
        {
            query = query.Where(p => p.FileHash == hash);
        }
        else if (finished.FileName is { } fileName)
        {
            query = query.Where(p => p.FileName == fileName);
        }
        else
        {
            // Nothing else correlates a finish with its start.
            logger.LogWarning("Not enough information in the print event to find the matching print.");
            return null;
        }

        if (finished.PrinterId is { } printerId)
        {
            query = query.Where(p => p.PrinterId == printerId);
        }

        var print = await query
            .OrderByDescending(p => p.CreatedDate)
            .Include(p => p.FilamentUsage!)
                .ThenInclude(pf => pf.Filament)
            .Include(p => p.Printer)
                .ThenInclude(pr => pr.LoadedFilaments)
            .FirstOrDefaultAsync();

        if (print is null)
        {
            logger.LogWarning("Matching print was not found.");
            return null;
        }

        print.Status = finished.Status;
        print.PrintTimeInSeconds = finished.PrintTimeInSeconds;
        print.UpdatedById = finished.UserId;
        context.Entry(print).State = EntityState.Modified;

        // Moonraker's per-spool reports (#171), when the notifier collected any, are the better
        // account of the job than its single total, which cannot say which spool it came from.
        var accumulated = await context.SpoolmanUsages.Where(u => u.PrintId == print.Id).ToListAsync();
        var replaced = accumulated.Count > 0 && await ReplaceUsageWithAccumulations(print, accumulated, finished.UserId);
        context.SpoolmanUsages.RemoveRange(accumulated);

        if (replaced)
        {
            await printService.UpdateFilamentUsageWeights(print);
        }
        else if (finished.ActualLengthInM is { } lengthInM)
        {
            if (print.FilamentUsage!.Count > 0)
            {
                var first = print.FilamentUsage.ElementAt(0);
                first.LengthInM = lengthInM;
                first.Source = PrintFilament.SourceMeasurement.Length;
            }
            else
            {
                var loaded = print.Printer.LoadedFilaments ?? new List<PrinterFilament>();
                print.FilamentUsage.Add(new PrintFilament
                {
                    EstimatedSource = PrintFilament.SourceMeasurement.Length,
                    Id = Guid.Empty,
                    FilamentId = SpoolInSlot(loaded, 0),
                    EstimatedLengthInM = lengthInM,
                    Source = PrintFilament.SourceMeasurement.Length,
                    LengthInM = lengthInM,
                    Notes = "",
                });
            }

            await printService.UpdateFilamentUsageWeights(print);
        }

        StoredSnapshot? snapshot = null;
        if (finished.Snapshot is not null)
        {
            var maxImages = await printService.GetMaxImagesPerPrint(finished.UserId);
            var existingImageCount = await context.PrintImages.CountAsync(pi => pi.PrintId == print.Id);
            if (existingImageCount < maxImages)
            {
                // The print may already have an image from its start.
                var maxDisplayOrder = await context.PrintImages
                    .Where(pi => pi.PrintId == print.Id)
                    .MaxAsync(pi => (int?)pi.DisplayOrder) ?? -1;
                snapshot = await AddSnapshot(print, finished.Snapshot, finished.UserId, maxDisplayOrder + 1);
                if (snapshot is not null)
                {
                    var fileId = snapshot.FileId;

                    // The newest snapshot becomes the default.
                    var otherDefaults = await context.PrintImages
                        .Where(p => p.PrintId == print.Id && p.IsDefault == true && p.FileId != fileId)
                        .ToListAsync();
                    otherDefaults.ForEach(p => p.IsDefault = false);
                }
            }
        }

        if (!await PrintFinishTransition.SaveIfStillUnfinished(context, print, finished.UserId))
        {
            // An overlapping delivery finished this print first (#199). Run again against what it
            // stored, exactly as a later redelivery would: the print no longer matches, so this
            // one changes nothing and does not notify a second time.
            context.ChangeTracker.Clear();
            // A loss can also be this request's own commit seen through a retried save (see
            // PrintFinishTransition), so the cache may be stale either way. Invalidating is cheap.
            cacheVersionService.InvalidateUserCache(finished.UserId);
            if (snapshot is not null)
            {
                await DeleteOrphanedSnapshot(snapshot);
            }
            if (retryIfRaced)
            {
                return await Finished(finished, retryIfRaced: false);
            }
            logger.LogWarning("The matching print was finished by another request.");
            return null;
        }
        cacheVersionService.InvalidateUserCache(finished.UserId);

        // Only a Printing print matches above, and the save claimed its move out of Printing, so
        // this is the transition to finished and no other request notifies it.
        await printFinishNotifier.NotifyIfJustFinished(print, PrintStatus.Printing);

        return print;
    }

    public async Task<bool> DropNotifierEventForBridge(long userId, long printerId)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        // One statement, so concurrent events cannot lose an increment.
        var dropped = await LiveBridges(userId, printerId, now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.DroppedNotifierEventCount, c => c.DroppedNotifierEventCount + 1)
                .SetProperty(c => c.LastDroppedNotifierEventAt, now));

        return dropped > 0;
    }

    public Task<bool> IsBridgeLive(long userId, long printerId) =>
        LiveBridges(userId, printerId, clock.GetUtcNow().UtcDateTime).AnyAsync();

    /// <summary>
    /// The printer's bridge connections seen within <see cref="Connection.StaleAfter"/> of
    /// <paramref name="now"/>. A stale bridge does not count: while it is down, the notifier is the
    /// only thing logging the printer.
    /// <para>
    /// Moonraker bridges only, not every <see cref="Connection.BridgeKinds"/> entry
    /// (printlog-bridge#82). The drop count drives the "remove <c>[notifier]</c> from
    /// moonraker.conf" notice on the connection, which only means something on a Moonraker one,
    /// and Spoolman <c>/use</c> must agree with the drop: it lands only on a notifier print, which
    /// exists exactly when its events were not dropped. The config block already leaves the
    /// notifier out for a printer with a bridge of any kind.
    /// </para>
    /// </summary>
    private IQueryable<Connection> LiveBridges(long userId, long printerId, DateTime now)
    {
        var liveSince = now - Connection.StaleAfter;
        return context.Connections
            .Where(c => c.UserId == userId
                && c.PrinterId == printerId
                && c.Kind == Connection.MoonrakerKind
                && c.LastSeenAt > liveSince);
    }

    /// <summary>
    /// Replaces the print's usage rows with one per accumulated spool, charging each what Moonraker
    /// reported for it. A spool deleted since (filaments are hard-deleted) is skipped.
    /// </summary>
    /// <returns>False, leaving the print's usage as it was, when no accumulated spool still exists.</returns>
    private async Task<bool> ReplaceUsageWithAccumulations(Print print, List<SpoolmanUsage> accumulated, long userId)
    {
        var ids = accumulated.Select(u => u.FilamentId).ToList();
        var existing = (await context.Filaments
            .Where(f => ids.Contains(f.Id) && f.CreatedById == userId)
            .Select(f => f.Id)
            .ToListAsync()).ToHashSet();
        var kept = accumulated.Where(u => existing.Contains(u.FilamentId)).OrderBy(u => u.UpdatedAt).ToList();
        if (kept.Count == 0)
        {
            return false;
        }

        // One spool keeps the slot and estimate the started event gave its usage, but a slot only on
        // a single-tool printer (the started row's 0 says nothing about where a multi-slot spool sits).
        // Several spools cannot share either. The actual length is never copied into the estimate:
        // that would put a perfect sample into the accuracy analytics.
        var started = kept.Count == 1 ? print.FilamentUsage!.FirstOrDefault() : null;
        var slot = print.Printer.SlotCount > 1 ? null : started?.Slot;

        context.PrintFilament.RemoveRange(print.FilamentUsage!);
        print.FilamentUsage!.Clear();
        foreach (var usage in kept)
        {
            // A spool Moonraker took back more from than it gave records nothing, never a negative length.
            var lengthInM = Math.Round(Math.Max(0, usage.LengthMm) / 1000, 3);
            print.FilamentUsage.Add(new PrintFilament
            {
                EstimatedSource = started?.EstimatedSource ?? PrintFilament.SourceMeasurement.Length,
                Id = Guid.Empty,
                FilamentId = usage.FilamentId,
                Slot = slot,
                EstimatedAmountMg = started?.EstimatedAmountMg,
                EstimatedLengthInM = started?.EstimatedLengthInM,
                EstimatedVolumeMl = started?.EstimatedVolumeMl,
                Source = PrintFilament.SourceMeasurement.Length,
                LengthInM = lengthInM,
                Notes = "Reported by Moonraker's Spoolman integration",
            });
        }
        return true;
    }

    /// <summary>
    /// The spool loaded in <paramref name="slot"/>. Slot 0 falls back to a spool loaded with no
    /// slot, which is how every single-tool printer was loaded before slots existed.
    /// </summary>
    private static Guid? SpoolInSlot(IEnumerable<PrinterFilament> loaded, int slot)
    {
        var rows = loaded.ToList();
        var inSlot = rows.FirstOrDefault(pf => pf.Slot == slot);
        if (inSlot is null && slot == 0)
        {
            inSlot = rows.Where(pf => pf.Slot is null).OrderBy(pf => pf.LoadedDateTime).FirstOrDefault();
        }
        return inSlot?.FilamentId;
    }

    private async Task<Print?> FindByExternalId(PrintStartedEvent started)
    {
        var printId = await context.Prints
            .Where(p => p.CreatedById == started.UserId
                && p.ExternalSource == started.ExternalSource
                && p.ExternalId == started.ExternalId)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync();
        return printId is { } id ? await printService.GetPrintById(id) : null;
    }

    /// <summary>The original's File id and every blob name one snapshot was uploaded under.</summary>
    private sealed record StoredSnapshot(Guid FileId, IReadOnlyList<string> BlobNames);

    /// <summary>
    /// Processes <paramref name="image"/> like any print image upload (decoded, re-encoded,
    /// thumbnail) and attaches it as the print's default image. Returns null, attaching nothing,
    /// when the snapshot is not a supported image: a bad camera frame must not fail the event.
    /// </summary>
    private async Task<StoredSnapshot?> AddSnapshot(Print print, IFormFile image, long userId, int displayOrder)
    {
        ProcessedImage processed;
        try
        {
            await using var uploadFileStream = image.OpenReadStream();
            processed = await imageProcessing.ProcessAsync(uploadFileStream, ImageProcessingOptions.PrintUploads);
        }
        catch (InvalidImageException ex)
        {
            logger.LogWarning("Skipped a snapshot that is not a supported image: {Reason}", ex.Message);
            return null;
        }

        var uploadedBlobNames = new List<string>();
        StoredImageFiles files;
        try
        {
            files = await imageWriter.WriteAsync(printImageContainerName, processed, userId, uploadedBlobNames);
        }
        catch
        {
            await imageWriter.DeleteBlobsQuietlyAsync(printImageContainerName, uploadedBlobNames);
            throw;
        }

        var snapshot = new PrintImage
        {
            CreatedById = userId,
            UpdatedById = userId,
            Print = print,
            IsDefault = true,
            DisplayOrder = displayOrder,
        };
        files.ApplyTo(snapshot, processed);
        context.PrintImages.Add(snapshot);
        return new StoredSnapshot(files.Original.Id, uploadedBlobNames);
    }

    /// <summary>
    /// Deletes the blobs of a snapshot whose save lost a race, unless its file row was stored
    /// after all: a commit whose retried save reported a loss did store it, and the image is
    /// then live. The original and its thumbnail are saved together, so the original's row
    /// answers for both. Best effort: a blob left behind costs storage, not correctness, so a
    /// failed delete is logged and does not fail the event.
    /// </summary>
    private async Task DeleteOrphanedSnapshot(StoredSnapshot snapshot)
    {
        try
        {
            if (await context.Files.AnyAsync(f => f.Id == snapshot.FileId))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check whether snapshot {FileId} was stored.", snapshot.FileId);
            return;
        }

        foreach (var blobName in snapshot.BlobNames)
        {
            try
            {
                await blobStorageService.DeleteBlobAsync(printImageContainerName, blobName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not delete the unreferenced snapshot {BlobName}.", blobName);
            }
        }
    }

    /// <summary>The user's last choice of whether comments are allowed, or false.</summary>
    private async Task<bool> DefaultAllowComments(long userId)
    {
        const int lastSelectedAllowCommentsUserSettingTypeId = 3;
        var value = await context.UserSettings
            .Where(u => u.UserId == userId && u.UserSettingTypeId == lastSelectedAllowCommentsUserSettingTypeId)
            .Select(u => u.Value)
            .FirstOrDefaultAsync();
        return bool.TryParse(value, out var allowComments) && allowComments;
    }

    /// <summary>The user's default view status, or Private.</summary>
    private async Task<PrintViewStatus> DefaultViewStatus(long userId)
    {
        const int defaultViewStatusUserSettingTypeId = 1;
        var value = await context.UserSettings
            .Where(u => u.UserId == userId && u.UserSettingTypeId == defaultViewStatusUserSettingTypeId)
            .Select(u => u.Value)
            .FirstOrDefaultAsync();
        return Enum.TryParse(value, out PrintViewStatus viewStatus) ? viewStatus : PrintViewStatus.Private;
    }
}
