namespace PrintLogApi.Models.DTOs.UserSetting;

public class UserSettingDto
{
    public long Id { get; set; }

    public long? UserId { get; set; }

    public int UserSettingTypeId { get; set; }

    public string? Value { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime UpdatedDate { get; set; }

}
