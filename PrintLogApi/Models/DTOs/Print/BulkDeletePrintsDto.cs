namespace PrintLogApi.Models.DTOs.Print;

/// <summary>
/// Permanently deletes many prints in one request, along with their comments, images,
/// attachments, filament usage, and notifications.
/// </summary>
public class BulkDeletePrintsDto
{
    /// <summary>The prints to delete. Must be non-empty, distinct, and at most 200 entries.</summary>
    public List<long> PrintIds { get; set; } = [];
}
