using System.ComponentModel.DataAnnotations;
using PrintLogApi.Models.DTOs.Common;

namespace PrintLogApi.Models.DTOs.UserApiKeys;

public class UserApiKeyDto : TimestampDto
{
    /// <summary>
    /// Not the API Key, just the primary key
    /// </summary>
    public Guid Id { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    public bool IsDeleted { get; set; }
}
