using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

public class UserSetting : TimestampEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public long? UserId { get; set; }
    public User? User { get; set; }

    public int UserSettingTypeId { get; set; }
    public UserSettingType UserSettingType { get; set; } = null!;

    [StringLength(250)]
    public string? Value { get; set; }
}
