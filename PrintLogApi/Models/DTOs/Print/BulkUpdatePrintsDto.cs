using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print;

/// <summary>
/// Applies the same set of field values to many prints in one request. Every field is
/// optional; omitted fields are left untouched. Enum values travel as their integer value,
/// matching the rest of the API.
/// </summary>
public class BulkUpdatePrintsDto
{
    /// <summary>The prints to update. Must be non-empty, distinct, and at most 200 entries.</summary>
    public List<long> PrintIds { get; set; } = [];

    public PrintStatus? Status { get; set; }

    /// <summary>Assign to an existing project. The project must belong to the caller.</summary>
    public Guid? ProjectId { get; set; }

    public PrintViewStatus? ViewStatus { get; set; }

    /// <summary>Reassign to one of the caller's printers.</summary>
    public long? PrinterId { get; set; }

    public bool? AllowComments { get; set; }

    public bool? AllowFileDownloads { get; set; }

    /// <summary>
    /// Field names to reset to null. Only "projectId" is clearable. A field may not be both
    /// set and cleared in one request.
    /// </summary>
    public List<string>? Clear { get; set; }
}
