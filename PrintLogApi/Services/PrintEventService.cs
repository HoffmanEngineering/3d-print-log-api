using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Services;

public class PrintEventService(
    PrintLogContext context,
    IPrintService printService,
    INotificationService notificationService,
    IBlobStorageService blobStorageService,
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

        if (started.Snapshot is not null && await printService.GetMaxImagesPerPrint(started.UserId) > 0)
        {
            // A new print has no images yet, so this one is first and the default.
            await AddSnapshot(print, started.Snapshot, started.UserId, displayOrder: 0);
        }

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A racing delivery of the same start won IX_Prints_User_ExternalSource_ExternalId.
            context.ChangeTracker.Clear();
            if (await FindByExternalId(started) is { } winner)
            {
                return new PrintStartedResult(winner, WasReplayed: true);
            }
            throw;
        }

        // Saving straight through the context skips the invalidation the controllers do, so the
        // cached print summary and analytics would keep serving pre-print figures.
        cacheVersionService.InvalidateUserCache(started.UserId);
        return new PrintStartedResult(print, WasReplayed: false);
    }

    public async Task<Print?> Finished(PrintFinishedEvent finished)
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

        if (finished.ActualLengthInM is { } lengthInM)
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
                var fileId = await AddSnapshot(print, finished.Snapshot, finished.UserId, maxDisplayOrder + 1);

                // The newest snapshot becomes the default.
                var otherDefaults = await context.PrintImages
                    .Where(p => p.PrintId == print.Id && p.IsDefault == true && p.FileId != fileId)
                    .ToListAsync();
                otherDefaults.ForEach(p => p.IsDefault = false);
            }
        }

        await context.SaveChangesAsync();
        cacheVersionService.InvalidateUserCache(finished.UserId);

        if (finished.Status == PrintStatus.Success)
        {
            await notificationService.CreatePrintCompletedNotification(finished.UserId, print.Id, print.Title);
        }
        else
        {
            await notificationService.CreatePrintFailedNotification(finished.UserId, print.Id, print.Title);
        }

        return print;
    }

    public async Task<bool> DropNotifierEventForBridge(long userId, long printerId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var liveSince = now - Connection.StaleAfter;

        // One statement, so concurrent events cannot lose an increment. A stale bridge does not
        // count: while it is down, the notifier is the only thing logging the printer.
        var dropped = await context.Connections
            .Where(c => c.UserId == userId
                && c.PrinterId == printerId
                && c.Kind == Connection.MoonrakerKind
                && c.LastSeenAt > liveSince)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.DroppedNotifierEventCount, c => c.DroppedNotifierEventCount + 1)
                .SetProperty(c => c.LastDroppedNotifierEventAt, now));

        return dropped > 0;
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

    /// <summary>Uploads <paramref name="image"/> and attaches it as the print's default image. Returns the file id.</summary>
    private async Task<Guid> AddSnapshot(Print print, IFormFile image, long userId, int displayOrder)
    {
        var fileId = Guid.NewGuid();
        var fileName = fileId + Path.GetExtension(image.FileName);

        using var uploadFileStream = image.OpenReadStream();
        var uploadResult = await blobStorageService.UploadAsync(printImageContainerName, fileName, uploadFileStream);

        var file = new Models.File
        {
            Size = image.Length,
            Path = uploadResult.BlobPath,
            Id = fileId,
            CreatedById = userId,
            UpdatedById = userId,
        };
        context.Files.Add(file);
        context.PrintImages.Add(new PrintImage
        {
            File = file,
            CreatedById = userId,
            UpdatedById = userId,
            Print = print,
            IsDefault = true,
            DisplayOrder = displayOrder,
        });
        return fileId;
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
