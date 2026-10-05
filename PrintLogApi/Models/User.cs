using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

public class User
{
    /// <summary>
    /// The View Access for the user's profile.
    /// </summary>
    public enum ProfileViewStatus
    {
        /// <summary>
        /// Anyone can search for and view
        /// </summary>
        Public = 1,

        /// <summary>
        /// Anyone with the direct link to the user can view
        /// </summary>
        Unlisted = 2,

        /// <summary>
        /// Only those who are friends with the user can view.
        /// </summary>
        Friends = 3,

        /// <summary>
        /// No one but the user can view.
        /// </summary>
        Private = 4,
    }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public string? OAuthUserId { get; set; }

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

    [StringLength(1000)]
    public string? Bio { get; set; }

    /// <summary>
    ///   If present, the datetime that the user started the deactivation process.
    /// </summary>
    public DateTimeOffset? DeactivationDateTime { get; set; }

    public ProfileViewStatus ViewStatus { get; set; }

    public ICollection<Printer>? printers { get; set; }

    /// <summary>
    /// The achievement catalog version this user was last fully evaluated against. 0 means never,
    /// so the first pass is the launch catch-up.
    /// </summary>
    public int AchievementCatalogVersion { get; set; }

    /// <summary>
    /// The account email, lower-cased and trimmed, copied from Auth0 (access-token claims or the
    /// offline backfill). Auth0 remains the source of truth; this copy exists so email can be sent
    /// and segmented without a Management API call per user.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>Auth0's <c>email_verified</c>. Only verified addresses are ever emailed.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>When <see cref="Email"/> or <see cref="EmailVerified"/> last actually changed. Not a "last compared" time: the sync writes only on change.</summary>
    public DateTimeOffset? EmailUpdatedAt { get; set; }

    /// <summary>Signup time. Null for accounts created before the column existed, unless backfilled from Auth0.</summary>
    public DateTimeOffset? CreatedDate { get; set; }
}
