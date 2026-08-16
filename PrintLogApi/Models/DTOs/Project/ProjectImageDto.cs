namespace PrintLogApi.Models.DTOs.Project;

public class ProjectImageDto
{
    public int Id { get; set; }
    public bool IsDefault { get; set; }
    public int DisplayOrder { get; set; }
    public string? Url { get; set; }
}
