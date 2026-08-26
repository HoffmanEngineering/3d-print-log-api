namespace PrintLogApi.Models.DTOs.Filament;

public class FilamentImageDto
{
    public int Id { get; set; }

    /// <summary>Signed, inline, bucketed-expiry URL. Null if signing failed.</summary>
    public string? Url { get; set; }

    /// <summary>Thumbnail URL; falls back to <see cref="Url"/> when no thumbnail exists.</summary>
    public string? ThumbnailUrl { get; set; }

    public bool IsDefault { get; set; }

    public int DisplayOrder { get; set; }
}
