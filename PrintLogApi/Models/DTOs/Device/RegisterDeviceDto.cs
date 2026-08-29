using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Device;

public class RegisterDeviceDto
{
    [Required]
    [MaxLength(512)]
    public string Token { get; set; } = null!;

    [Required]
    public DevicePlatform Platform { get; set; }

    [MaxLength(50)]
    public string? AppVersion { get; set; }
}
