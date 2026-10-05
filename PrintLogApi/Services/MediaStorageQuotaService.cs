using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;

namespace PrintLogApi.Services;

/// <inheritdoc cref="IMediaStorageQuotaService"/>
public class MediaStorageQuotaService(PrintLogContext context) : IMediaStorageQuotaService
{
    public async Task<long> GetUsedBytesAsync(long userId, CancellationToken ct = default)
    {
        var attachmentBytes = await context.PrintAttachments
            .Where(pa => pa.CreatedById == userId)
            .SumAsync(pa => (long?)pa.File.Size, ct) ?? 0L;

        var filamentImageBytes = await context.FilamentImages
            .Where(fi => fi.CreatedById == userId)
            .SumAsync(fi => (long?)fi.File.Size + (fi.ThumbnailFile != null ? fi.ThumbnailFile.Size : 0L), ct) ?? 0L;

        // Ownership reaches through the navigation: CreatedById records the uploader, which
        // is not the same person as the owner once an image can be added on someone's behalf.
        var printerImageBytes = await context.PrinterImages
            .Where(pi => pi.Printer.UserId == userId)
            .SumAsync(pi => (long?)pi.File.Size + (pi.ThumbnailFile != null ? pi.ThumbnailFile.Size : 0L), ct) ?? 0L;

        return attachmentBytes + filamentImageBytes + printerImageBytes;
    }

    /// <summary>
    /// Every user is measured against the Pro ceiling. FreeMaxFileStorageBytes governs file
    /// attachments only and is deliberately not applied here.
    /// </summary>
    public async Task EnsureCapacityAsync(long userId, long newBytes, CancellationToken ct = default)
    {
        if (await GetUsedBytesAsync(userId, ct) + newBytes > SubscriptionLimits.ProMaxFileStorageBytes)
            throw new BadRequestException("Storage quota exceeded. Delete files to free up space.");
    }
}
