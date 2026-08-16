namespace PrintLogApi.Models.DTOs.Print;

public class PrintAttachmentDto
{
    public long Id { get; set; }
    public string? OriginalFileName { get; set; }
    public long SizeBytes { get; set; }
    public string? ContentType { get; set; }
    public int DisplayOrder { get; set; }
}
