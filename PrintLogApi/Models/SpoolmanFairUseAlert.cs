using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// When the admin was last emailed about one fair-use signal for one user (#175). The row is the
/// cool-down: at most one email per user per signal per <see cref="Cooldown"/>.
/// </summary>
public class SpoolmanFairUseAlert
{
    public const string ConnectionCountSignal = "connection_count";
    public const string NetworkCountSignal = "network_count";

    public static readonly TimeSpan Cooldown = TimeSpan.FromDays(7);

    [Key]
    public long Id { get; set; }

    public long UserId { get; set; }
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;

    [Required]
    [MaxLength(50)]
    public string Signal { get; set; } = null!;

    public DateTime LastSentAt { get; set; }
}
