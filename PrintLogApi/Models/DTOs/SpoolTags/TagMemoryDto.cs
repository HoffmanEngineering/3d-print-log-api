namespace PrintLogApi.Models.DTOs.SpoolTags;

/// <summary>A raw tag memory dump as the client read it. Kind is "classic", "type2" or "nfcv".</summary>
public class TagMemoryDto
{
    public string? Kind { get; set; }
    public string? Data { get; set; }
    public string? KeyFormat { get; set; }
    public List<int>? Unreadable { get; set; }
}
