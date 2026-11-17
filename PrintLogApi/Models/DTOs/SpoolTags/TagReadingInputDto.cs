using PrintLogApi.SpoolTags;

namespace PrintLogApi.Models.DTOs.SpoolTags;

/// <summary>
/// A decoded tag reading a client sends along with a spool's <c>TagUids</c>. It is applied to the
/// tag with the same UID, and ignored when that UID is not among the spool's tags.
/// </summary>
public class TagReadingInputDto
{
    /// <summary>The tag UID the reading came from, in any case and with any separators.</summary>
    public string? Uid { get; set; }

    public TagReading? Reading { get; set; }
}
