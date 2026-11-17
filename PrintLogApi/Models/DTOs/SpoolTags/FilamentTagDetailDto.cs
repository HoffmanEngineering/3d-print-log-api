using PrintLogApi.SpoolTags;

namespace PrintLogApi.Models.DTOs.SpoolTags;

/// <summary>A tag on a spool with its last decoded reading, when it has one.</summary>
public class FilamentTagDetailDto
{
    public string? Uid { get; set; }
    public string? Format { get; set; }
    public TagReading? Reading { get; set; }
    public DateTime? LastReadAt { get; set; }
}
