using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Services;

public interface IFileAttachmentService
{
    Task<GetUploadUrlResponse> GetUploadUrlAsync(long printId, long userId, GetUploadUrlRequest request);
    Task<PrintAttachmentDto> ConfirmUploadAsync(long printId, long userId, ConfirmUploadRequest request);
    Task<IEnumerable<PrintAttachmentDto>> GetFilesAsync(long printId);
    Task<GetDownloadUrlResponse> GetDownloadUrlAsync(long printId, long fileId, long? userId);
    Task DeleteFileAsync(long printId, long fileId, long userId);
}
