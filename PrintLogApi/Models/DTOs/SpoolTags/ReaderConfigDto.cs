namespace PrintLogApi.Models.DTOs.SpoolTags;

/// <summary>How the native app derives the sector keys for each tag format it can read.</summary>
public class ReaderConfigDto
{
    public List<ReaderFormatDto>? Formats { get; set; }
}

/// <summary>One tag format: its id, the key-derivation scheme and that scheme's parameters.</summary>
public class ReaderFormatDto
{
    public string? Id { get; set; }
    public string? Scheme { get; set; }
    public Dictionary<string, string>? Params { get; set; }
}
