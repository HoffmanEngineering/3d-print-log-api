namespace PrintLogApi.Models.DTOs.Filament;

public class SetFilamentActiveDto
{
    /// <summary>Required. Omitting it is a 400, never a silent retire.</summary>
    public bool? IsActive { get; set; }
}
