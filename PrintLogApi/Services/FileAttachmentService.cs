using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using FileModel = PrintLogApi.Models.File;

namespace PrintLogApi.Services
{
    public class FileAttachmentService : IFileAttachmentService
    {
        private readonly PrintLogContext _context;
        private readonly IBlobStorageService _blobStorageService;

        private const string AttachmentContainer = "printattachments";
        private const int MaxFilesPerPrintPro = 5;
        private const long MaxFileStorageBytesPro = 50L * 1024 * 1024 * 1024;
        private const long MaxFileSizeBytes = 200L * 1024 * 1024; // 200MB
        private static readonly string[] AllowedExtensions = { ".gcode", ".stl", ".3mf", ".obj" };

        public FileAttachmentService(PrintLogContext context, IBlobStorageService blobStorageService)
        {
            _context = context;
            _blobStorageService = blobStorageService;
        }

        public async Task<GetUploadUrlResponse> GetUploadUrlAsync(long printId, long userId, GetUploadUrlRequest request)
        {
            var print = await _context.Prints.FindAsync(printId)
                ?? throw new NotFoundException($"Print {printId} not found.");

            if (print.CreatedById != userId)
                throw new ForbiddenException("You do not own this print.");

            await AssertProAsync(userId);
            await AssertFileQuotaAsync(printId, userId, request.SizeBytes);
            AssertAllowedExtension(request.FileName);

            var blobName = $"{printId}/{Guid.NewGuid()}{GetExtension(request.FileName)}";
            var sasUri = await _blobStorageService.GenerateSasUploadUrlAsync(
                AttachmentContainer, blobName, TimeSpan.FromMinutes(15));

            return new GetUploadUrlResponse
            {
                SasUrl = sasUri.ToString(),
                BlobPath = $"{AttachmentContainer}/{blobName}",
            };
        }

        public async Task<PrintAttachmentDto> ConfirmUploadAsync(long printId, long userId, ConfirmUploadRequest request)
        {
            var print = await _context.Prints.FindAsync(printId)
                ?? throw new NotFoundException($"Print {printId} not found.");

            if (print.CreatedById != userId)
                throw new ForbiddenException("You do not own this print.");

            await AssertProAsync(userId);

            // Parse container and blob name from the blobPath (format: "printattachments/123/guid.ext")
            var parts = request.BlobPath.Split('/', 2);
            if (parts.Length != 2 || parts[0] != AttachmentContainer)
                throw new BadRequestException("Invalid blob path.");

            var fileId = Guid.NewGuid();
            var file = new FileModel
            {
                Id = fileId,
                Path = request.BlobPath,
                Size = request.SizeBytes,
                CreatedById = userId,
                UpdatedById = userId,
            };
            _context.Files.Add(file);

            var displayOrder = await _context.PrintAttachments
                .Where(pa => pa.PrintId == printId)
                .CountAsync();

            var attachment = new PrintAttachment
            {
                PrintId = printId,
                FileId = fileId,
                OriginalFileName = request.FileName,
                ContentType = request.ContentType,
                DisplayOrder = displayOrder,
                CreatedById = userId,
                UpdatedById = userId,
            };
            _context.PrintAttachments.Add(attachment);
            await _context.SaveChangesAsync();

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
            return await _context.PrintAttachments
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
            var attachment = await _context.PrintAttachments
                .Include(pa => pa.File)
                .Include(pa => pa.Print)
                .Where(pa => pa.Id == fileId && pa.PrintId == printId)
                .SingleOrDefaultAsync()
                ?? throw new NotFoundException($"File {fileId} not found on print {printId}.");

            // Allow download if: owner, or AllowFileDownloads is true
            bool isOwner = userId.HasValue && attachment.Print.CreatedById == userId;
            if (!isOwner && !attachment.Print.AllowFileDownloads)
                throw new ForbiddenException("File downloads are not enabled for this print.");

            var blobPathParts = attachment.File.Path.Split('/', 2);
            var expiresIn = TimeSpan.FromHours(1);
            var sasUri = await _blobStorageService.GenerateSasDownloadUrlAsync(
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
            var attachment = await _context.PrintAttachments
                .Include(pa => pa.File)
                .Where(pa => pa.Id == fileId && pa.PrintId == printId)
                .SingleOrDefaultAsync()
                ?? throw new NotFoundException($"File {fileId} not found on print {printId}.");

            if (attachment.CreatedById != userId)
                throw new ForbiddenException("You do not own this file.");

            _context.PrintAttachments.Remove(attachment);
            _context.Files.Remove(attachment.File);
            await _context.SaveChangesAsync();

            // Note: blob is left in Azure storage (orphaned blobs cleaned up by a separate job or policy).
            // This matches the existing PrintImage deletion behavior.
        }

        // ── Private helpers ──────────────────────────────────────────────────

        private async Task AssertProAsync(long userId)
        {
            var subscription = await _context.Subscriptions
                .Where(s => s.UserId == userId)
                .AsNoTracking()
                .SingleOrDefaultAsync();

            if (subscription?.Status != SubscriptionStatus.Active)
                throw new ForbiddenException("File attachments require a Pro subscription.");
        }

        private async Task AssertFileQuotaAsync(long printId, long userId, long newFileSizeBytes)
        {
            // Per-print file count limit
            var fileCount = await _context.PrintAttachments
                .Where(pa => pa.PrintId == printId)
                .CountAsync();

            if (fileCount >= MaxFilesPerPrintPro)
                throw new BadRequestException($"Maximum of {MaxFilesPerPrintPro} files per print allowed.");

            // Per-user storage quota
            var usedBytes = await _context.PrintAttachments
                .Where(pa => pa.CreatedById == userId)
                .SumAsync(pa => (long?)pa.File.Size) ?? 0L;

            if (usedBytes + newFileSizeBytes > MaxFileStorageBytesPro)
                throw new BadRequestException("Storage quota exceeded. Delete files to free up space.");

            // Per-file size limit (also validated client-side and in the DTO range attribute)
            if (newFileSizeBytes > MaxFileSizeBytes)
                throw new BadRequestException("File size exceeds the 200MB limit.");
        }

        private static void AssertAllowedExtension(string fileName)
        {
            var ext = GetExtension(fileName).ToLowerInvariant();
            if (!Array.Exists(AllowedExtensions, e => e == ext))
                throw new BadRequestException(
                    $"File type '{ext}' is not supported. Allowed: {string.Join(", ", AllowedExtensions)}");
        }

        private static string GetExtension(string fileName)
            => System.IO.Path.GetExtension(fileName) ?? string.Empty;
    }
}
