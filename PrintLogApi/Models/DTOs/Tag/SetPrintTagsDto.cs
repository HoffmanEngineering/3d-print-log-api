using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Tag;

/// <summary>The complete set of tags a print should carry.</summary>
public class SetPrintTagsDto
{
    /// <summary>
    /// Ids of your own tags. Replaces the print's tags; an empty list removes them all.
    /// Duplicates are ignored.
    /// </summary>
    [MaxLength(Models.Tag.MaxTagsPerPrint)]
    public ICollection<Guid>? TagIds { get; set; }
}
