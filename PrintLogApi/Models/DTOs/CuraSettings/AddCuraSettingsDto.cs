using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.CuraSettings;

public class AddCuraSettingsDto
{
    [StringLength(100)]
    public string? CuraVersion { get; set; }

    [StringLength(100)]
    public string? PluginVersion { get; set; }

    public dynamic? Settings { get; set; }
}
