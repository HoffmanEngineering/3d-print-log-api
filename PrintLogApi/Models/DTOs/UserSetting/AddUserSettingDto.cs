using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.UserSetting;

public class AddUserSettingDto
{

    public int UserSettingTypeId { get; set; }

    [StringLength(250)]
    public string? Value { get; set; }
}
