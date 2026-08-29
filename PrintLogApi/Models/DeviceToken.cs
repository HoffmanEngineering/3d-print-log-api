using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// An FCM registration token for one app installation. Device state, not a preference:
/// tokens are device-scoped, survive logout, and must be prunable when FCM reports the
/// installation is gone.
///
/// Deliberately NOT a TimestampEntity: there is no meaningful "created by user" for a
/// device registration, and TimestampEntity's audit FKs are required.
/// </summary>
public class DeviceToken
{
    [Key]
    public long Id { get; set; }

    public long UserId { get; set; }
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;

    [Required]
    [MaxLength(512)]
    public string Token { get; set; } = null!;

    public DevicePlatform Platform { get; set; }

    [MaxLength(50)]
    public string? AppVersion { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime LastSeenDate { get; set; }
}
