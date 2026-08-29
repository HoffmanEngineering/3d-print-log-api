using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Device;

public class RegisterDeviceDto
{
    // Nullable with [Required], per the DTO rules in AGENTS.md: the annotation states what
    // actually arrives across the trust boundary, and required-ness is enforced by validation
    // rather than by `= null!`, which would assert something nothing enforces.
    [Required]
    [MaxLength(512)]
    public string? Token { get; set; }

    // [Required] alone does not reject 0 or an out-of-range integer on a non-nullable enum,
    // because default(DevicePlatform) is indistinguishable from "not supplied".
    [Required]
    [EnumDataType(typeof(DevicePlatform))]
    public DevicePlatform Platform { get; set; }

    [MaxLength(50)]
    public string? AppVersion { get; set; }
}
