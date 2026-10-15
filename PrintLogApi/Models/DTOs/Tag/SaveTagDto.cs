using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Tag;

/// <summary>Creates a tag, or replaces a tag's name and colour.</summary>
public class SaveTagDto
{
    /// <summary>
    /// The tag's name, unique per user ignoring case. Leading and trailing spaces are trimmed.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(Models.Tag.NameMaxLength)]
    public string? Name { get; set; }

    /// <summary>
    /// A <c>#rrggbb</c> colour, or null for the default. This is a full replace: omitting it on
    /// an update clears the colour.
    /// </summary>
    [RegularExpression("^#[0-9a-fA-F]{6}$", ErrorMessage = "Color must be a #rrggbb hex colour.")]
    public string? Color { get; set; }
}
