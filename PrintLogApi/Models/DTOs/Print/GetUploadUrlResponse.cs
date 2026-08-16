namespace PrintLogApi.Models.DTOs.Print;

public class GetUploadUrlResponse
{
    public string? SasUrl { get; set; }
    public string? BlobPath { get; set; }
}
