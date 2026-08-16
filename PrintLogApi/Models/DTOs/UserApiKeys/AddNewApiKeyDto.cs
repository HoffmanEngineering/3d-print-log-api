using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.UserApiKeys;

public class AddNewApiKeyDto
{
    [StringLength(1000)]
    public string? Description { get; set; }
}
