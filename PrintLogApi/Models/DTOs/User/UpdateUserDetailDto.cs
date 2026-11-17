using System.ComponentModel.DataAnnotations;
using static PrintLogApi.Models.User;

namespace PrintLogApi.Models.DTOs.User;

public class UpdateUserDetailDto
{
    /// <summary>
    /// URL of the user's profile picture. Set it by uploading to <c>POST /api/Users/me/profile-image</c>;
    /// a different value here is ignored, except the picture on the user's sign-in profile while
    /// the profile has none. Omitting it keeps the current picture.
    /// </summary>
    public string? ProfilePicture { get; set; }

    /// <summary>
    /// URL of the user's cover picture. Ignored: set it with <c>POST /api/Users/me/cover-image</c>
    /// and remove it with <c>DELETE /api/Users/me/cover-image</c>.
    /// </summary>
    public string? CoverPicture { get; set; }

    [StringLength(30, MinimumLength = 1)]
    public string? DisplayName { get; set; }

    [StringLength(1000)]
    public string? Bio { get; set; }

    public ProfileViewStatus ViewStatus { get; set; }
}
