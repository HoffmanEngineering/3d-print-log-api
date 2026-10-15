namespace PrintLogApi.Models.DTOs.Tag;

/// <summary>One of the caller's print tags.</summary>
public class TagDto
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    /// <summary>A lower-case <c>#rrggbb</c> colour, or null for the default.</summary>
    public string? Color { get; set; }
}
