namespace PrintLogApi.Models.DTOs.Slicer;

/// <summary>
/// One of an OrcaSlicer plugin install's recent uploads, as <c>GET /api/Slicer/uploads/pending</c>
/// lists it: enough to tell the uploads apart, never the note or the thumbnail.
/// </summary>
public class SlicerPendingUploadDto
{
    /// <summary>The setting's id: what the new-print page takes, and what claims the upload when the page loads it.</summary>
    public Guid SettingId { get; set; }

    /// <summary>When the upload was stored.</summary>
    public DateTimeOffset CreatedDate { get; set; }

    /// <summary>The print title derived from the output file name, or null when the plugin sent none.</summary>
    public string? PrintName { get; set; }

    /// <summary>The output file name (the last segment of the path the slicer wrote), or null.</summary>
    public string? FileName { get; set; }

    /// <summary>The slicer's print time estimate in whole seconds, or null when the G-code had none.</summary>
    public long? EstimatedPrintTimeSeconds { get; set; }

    /// <summary>The slicer's version, as stored (the G-code header's, else the plugin's).</summary>
    public string? SlicerVersion { get; set; }

    /// <summary>The plugin's version, as stored: <c>orca-plugin/&lt;version&gt;</c>.</summary>
    public string? PluginVersion { get; set; }

    /// <summary>
    /// True when the caller has already opened (claimed) this upload. Uploads another user
    /// claimed are never listed.
    /// </summary>
    public bool Claimed { get; set; }
}
