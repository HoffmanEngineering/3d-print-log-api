using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// One achievement tier a user has earned. Untiered badges use tier 1. The catalog that defines
/// the keys lives in code (<c>PrintLogApi.Achievements.AchievementCatalog</c>), never in the database.
/// </summary>
public class UserAchievement
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public long UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>The catalog key. Permanent once shipped: a key may be retired, never renamed.</summary>
    [MaxLength(64)]
    public string AchievementKey { get; set; } = null!;

    public int Tier { get; set; }

    /// <summary>When the tier was granted, in UTC.</summary>
    public DateTime UnlockedAt { get; set; }

    /// <summary>
    /// Granted by the launch backfill rather than earned live. Shown as "Earned before achievements
    /// existed" and never celebrated individually.
    /// </summary>
    public bool Retroactive { get; set; }
}
