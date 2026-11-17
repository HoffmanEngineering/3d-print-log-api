using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using FileModel = PrintLogApi.Models.File;

namespace PrintLogApi.Services;

public class FileAttachmentService(
    PrintLogContext context,
    IMediaStorageQuotaService quota,
    IBlobStorageService blobStorageService) : IFileAttachmentService
{
    private const string AttachmentContainer = "printattachments";
    private const long MaxFileSizeBytes = 200L * 1024 * 1024; // 200MB
    // Model files, and an MP4 video: the print's timelapse, which printlog-bridge attaches from
    // moonraker-timelapse (printlog-bridge#42).
    private static readonly string[] AllowedExtensions = { ".gcode", ".stl", ".3mf", ".obj", ".mp4" };

    public async Task<GetUploadUrlResponse> GetUploadUrlAsync(long printId, long userId, GetUploadUrlRequest request)
    {
        var print = await context.Prints.FindAsync(printId)
            ?? throw new NotFoundException($"Print {printId} not found.");

        if (print.CreatedById != userId)
            throw new ForbiddenException("You do not own this print.");

        await AssertProAsync(userId);
        await AssertFileQuotaAsync(printId, userId, request.SizeBytes);
        AssertAllowedExtension(request.FileName);

        var blobName = $"{printId}/{Guid.NewGuid()}{GetExtension(request.FileName)}";
        var sasUri = await blobStorageService.GenerateSasUploadUrlAsync(
            AttachmentContainer, blobName, TimeSpan.FromMinutes(15));

        return new GetUploadUrlResponse
        {
            SasUrl = sasUri.ToString(),
            BlobPath = $"{AttachmentContainer}/{blobName}",
        };
    }

    public async Task<PrintAttachmentDto> ConfirmUploadAsync(long printId, long userId, ConfirmUploadRequest request)
    {
        var print = await context.Prints.FindAsync(printId)
            ?? throw new NotFoundException($"Print {printId} not found.");

        if (print.CreatedById != userId)
            throw new ForbiddenException("You do not own this print.");

        await AssertProAsync(userId);

        // The client wrote the blob itself through a SAS URL, which can bound neither its size nor
        // stop it being confirmed twice, so nothing in the request is taken on trust. The path must
        // be one GetUploadUrlAsync issues for THIS print ("printattachments/{printId}/{guid}{ext}"),
        // the file name must carry that same allowed extension, and the size recorded is the
        // blob's own. None of these failures deletes the blob: a path that fails them may be
        // another print's file, or one already attached.
        var blobName = ParseIssuedBlobName(printId, request.BlobPath);
        AssertAllowedExtension(request.FileName);
        if (!string.Equals(GetExtension(request.FileName), GetExtension(blobName), StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("The file name's type does not match the uploaded file.");

        if (await context.Files.AnyAsync(f => f.Path == request.BlobPath))
            throw new BadRequestException("This file has already been attached.");

        var sizeBytes = await blobStorageService.GetBlobLengthAsync(AttachmentContainer, blobName)
            ?? throw new BadRequestException("The file was not uploaded.");

        // The blob is this print's, issued by upload-url and attached to nothing, so one that
        // breaks a limit is deleted rather than left to count against nothing.
        try
        {
            await AssertFileQuotaAsync(printId, userId, sizeBytes);
        }
        catch (BadRequestException)
        {
            await blobStorageService.DeleteBlobAsync(AttachmentContainer, blobName);
            throw;
        }

        var fileId = Guid.NewGuid();
        var file = new FileModel
        {
            Id = fileId,
            Path = request.BlobPath,
            Size = sizeBytes,
            CreatedById = userId,
            UpdatedById = userId,
        };
        context.Files.Add(file);

        var displayOrder = await context.PrintAttachments
            .Where(pa => pa.PrintId == printId)
            .CountAsync();

        var attachment = new PrintAttachment
        {
            PrintId = printId,
            FileId = fileId,
            OriginalFileName = request.FileName!,
            ContentType = request.ContentType!,
            DisplayOrder = displayOrder,
            CreatedById = userId,
            UpdatedById = userId,
        };
        context.PrintAttachments.Add(attachment);
        await context.SaveChangesAsync();

        return new PrintAttachmentDto
        {
            Id = attachment.Id,
            OriginalFileName = attachment.OriginalFileName,
            SizeBytes = file.Size,
            ContentType = attachment.ContentType,
            DisplayOrder = attachment.DisplayOrder,
        };
    }

    public async Task<IEnumerable<PrintAttachmentDto>> GetFilesAsync(long printId)
    {
        return await context.PrintAttachments
            .Where(pa => pa.PrintId == printId)
            .OrderBy(pa => pa.DisplayOrder)
            .Select(pa => new PrintAttachmentDto
            {
                Id = pa.Id,
                OriginalFileName = pa.OriginalFileName,
                SizeBytes = pa.File.Size,
                ContentType = pa.ContentType,
                DisplayOrder = pa.DisplayOrder,
            })
            .ToListAsync();
    }

    public async Task<GetDownloadUrlResponse> GetDownloadUrlAsync(long printId, long fileId, long? userId)
    {
        var attachment = await context.PrintAttachments
            .Include(pa => pa.File)
            .Include(pa => pa.Print)
            .Where(pa => pa.Id == fileId && pa.PrintId == printId)
            .SingleOrDefaultAsync()
            ?? throw new NotFoundException($"File {fileId} not found on print {printId}.");

        // Allow download if: owner, or AllowFileDownloads is true
        bool isOwner = userId.HasValue && attachment.Print.CreatedById == userId;

        // Private prints are only accessible to their owner
        if (attachment.Print.ViewStatus == Print.PrintViewStatus.Private && !isOwner)
            throw new ForbiddenException("This print is private.");

        // For non-private prints, check AllowFileDownloads
        if (!isOwner && !attachment.Print.AllowFileDownloads)
            throw new ForbiddenException("File downloads are not enabled for this print.");

        var blobPathParts = attachment.File.Path!.Split('/', 2);
        if (blobPathParts.Length != 2)
            throw new InvalidOperationException($"Stored blob path is invalid: {attachment.File.Path}");
        var expiresIn = TimeSpan.FromHours(1);
        var sasUri = await blobStorageService.GenerateSasDownloadUrlAsync(
            blobPathParts[0],
            blobPathParts[1],
            attachment.ContentType,
            attachment.OriginalFileName,
            expiresIn);

        return new GetDownloadUrlResponse
        {
            Url = sasUri.ToString(),
            ExpiresAt = DateTimeOffset.UtcNow.Add(expiresIn),
        };
    }

    public async Task DeleteFileAsync(long printId, long fileId, long userId)
    {
        var attachment = await context.PrintAttachments
            .Include(pa => pa.File)
            .Where(pa => pa.Id == fileId && pa.PrintId == printId)
            .SingleOrDefaultAsync()
            ?? throw new NotFoundException($"File {fileId} not found on print {printId}.");

        if (attachment.CreatedById != userId)
            throw new ForbiddenException("You do not own this file.");

        context.PrintAttachments.Remove(attachment);
        context.Files.Remove(attachment.File);
        await context.SaveChangesAsync();

        // Note: blob is left in Azure storage (orphaned blobs cleaned up by a separate job or policy).
        // This matches the existing PrintImage deletion behavior.
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private async Task AssertProAsync(long userId)
    {
        var subscription = await context.Subscriptions
            .Where(s => s.UserId == userId)
            .AsNoTracking()
            .SingleOrDefaultAsync();

        if (subscription?.Status != SubscriptionStatus.Active)
            throw new ForbiddenException("File attachments require a Pro subscription.");
    }

    private async Task AssertFileQuotaAsync(long printId, long userId, long newFileSizeBytes)
    {
        // Per-print file count limit
        var fileCount = await context.PrintAttachments
            .Where(pa => pa.PrintId == printId)
            .CountAsync();

        if (fileCount >= SubscriptionLimits.ProMaxFilesPerPrint)
            throw new BadRequestException($"Maximum of {SubscriptionLimits.ProMaxFilesPerPrint} files per print allowed.");

        // Per-user storage quota. The per-print FILE COUNT ceiling above is
        // attachment-specific and stays here; the byte figure comes from the shared
        // calculator so it matches what the user is shown and what images are held to.
        var usedBytes = await quota.GetUsedBytesAsync(userId);

        if (usedBytes + newFileSizeBytes > SubscriptionLimits.ProMaxFileStorageBytes)
            throw new BadRequestException("Storage quota exceeded. Delete files to free up space.");

        // Per-file size limit (also validated client-side and in the DTO range attribute)
        if (newFileSizeBytes > MaxFileSizeBytes)
            throw new BadRequestException("File size exceeds the 200MB limit.");
    }

    /// <summary>
    /// The blob name (within the container) of a path GetUploadUrlAsync issued for this print:
    /// "printattachments/{printId}/{guid}{ext}", the GUID in its default form and the extension
    /// an allowed one, in whatever case the uploaded file name used. Anything else is a 400.
    /// </summary>
    private static string ParseIssuedBlobName(long printId, string? blobPath)
    {
        var prefix = $"{AttachmentContainer}/{printId}/";
        if (blobPath is null || !blobPath.StartsWith(prefix, StringComparison.Ordinal))
            throw new BadRequestException("Invalid blob path.");

        var name = blobPath[prefix.Length..];
        var ext = GetExtension(name);
        // Length too: Guid parsing forgives surrounding whitespace, which the issued name never has.
        if (ext.Length == 0
            || name.Length - ext.Length != 36
            || !Array.Exists(AllowedExtensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))
            || !Guid.TryParseExact(name[..^ext.Length], "D", out _))
            throw new BadRequestException("Invalid blob path.");

        return $"{printId}/{name}";
    }

    private static void AssertAllowedExtension(string? fileName)
    {
        var ext = GetExtension(fileName).ToLowerInvariant();
        if (!Array.Exists(AllowedExtensions, e => e == ext))
            throw new BadRequestException(
                $"File type '{ext}' is not supported. Allowed: {string.Join(", ", AllowedExtensions)}");
    }

    private static string GetExtension(string? fileName)
        => System.IO.Path.GetExtension(fileName) ?? string.Empty;
}
