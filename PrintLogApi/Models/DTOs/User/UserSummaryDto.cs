using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.User;

public class UserSummaryDto
{
    public long Id { get; set; }

    /// <summary>
    /// URL pointing to the user's profile picture.
    /// </summary>
    public string? ProfilePicture { get; set; }

    /// <summary>
    /// URL pointing to the user's cover picture.
    /// </summary>
    public string? CoverPicture { get; set; }

    [StringLength(30, MinimumLength = 1)]
    public string? DisplayName { get; set; }

}
