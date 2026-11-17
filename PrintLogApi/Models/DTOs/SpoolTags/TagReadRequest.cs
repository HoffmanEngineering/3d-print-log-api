namespace PrintLogApi.Models.DTOs.SpoolTags;

public class TagReadRequest
{
    public string? Uid { get; set; }
    public List<string>? Techs { get; set; }
    public TagMemoryDto? Memory { get; set; }
}
