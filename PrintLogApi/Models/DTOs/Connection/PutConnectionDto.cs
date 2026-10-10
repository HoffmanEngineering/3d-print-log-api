using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Connection;

/// <summary>
/// What an agent sends when it starts and on every heartbeat. Each call replaces these fields, so
/// send the printer binding every time: leaving it out unbinds the connection.
/// </summary>
public class PutConnectionDto : IValidatableObject
{
    /// <summary>The connector type, e.g. <c>moonraker</c>. Stored lower-case.</summary>
    [StringLength(50)]
    public string? Kind { get; set; }

    /// <summary>The name shown on prints it logs: "Logged automatically by …".</summary>
    [StringLength(100)]
    public string? DisplayName { get; set; }

    [StringLength(50)]
    public string? AgentVersion { get; set; }

    /// <summary>The printer the user chose at pairing. Must be one of yours.</summary>
    public long? PrinterId { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Kind))
        {
            yield return new ValidationResult("kind is required.", [nameof(Kind)]);
        }
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            yield return new ValidationResult("displayName is required.", [nameof(DisplayName)]);
        }
    }
}
