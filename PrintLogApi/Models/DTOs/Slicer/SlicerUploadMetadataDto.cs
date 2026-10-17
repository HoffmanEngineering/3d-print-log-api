namespace PrintLogApi.Models.DTOs.Slicer;

/// <summary>
/// The <c>metadata</c> part of <c>POST /api/Slicer/uploads</c>: what the OrcaSlicer plugin knows
/// that the G-code text does not carry. Unknown fields are ignored.
/// </summary>
public class SlicerUploadMetadataDto
{
    /// <summary>Required. The plugin's version; stored as <c>orca-plugin/&lt;pluginVersion&gt;</c>.</summary>
    public string? PluginVersion { get; set; }

    /// <summary>The slicer's version (<c>orca_version</c>). Used only when the G-code header names none.</summary>
    public string? SlicerVersion { get; set; }

    /// <summary>The path or file name the slicer wrote; sets the print's file name and title.</summary>
    public string? OutputName { get; set; }

    /// <summary><c>File</c> for an export, otherwise the print host the slicer uploaded to.</summary>
    public string? Host { get; set; }

    /// <summary>Required. The note template: <c>default</c> or <c>full</c>, any case.</summary>
    public string? Template { get; set; }

    /// <summary>
    /// The plugin's install id: exactly 32 lowercase hex characters (Python's <c>uuid4().hex</c>).
    /// Optional; when sent, <c>GET /api/Slicer/uploads/pending</c> can list the upload for that install.
    /// </summary>
    public string? InstallId { get; set; }

    /// <summary>Each object instance's extrusion bounds, for the note's models section. At most 1,000.</summary>
    public List<SlicerUploadModelDto?>? Models { get; set; }

    /// <summary>The user preset files the G-code names, for the note's profile changes. At most 64.</summary>
    public List<SlicerUploadPresetDto?>? UserPresets { get; set; }
}

/// <summary>One object instance's centerline extrusion bounds in bed coordinates, in millimeters.</summary>
public class SlicerUploadModelDto
{
    public string? Name { get; set; }
    public double? MinX { get; set; }
    public double? MaxX { get; set; }
    public double? MinY { get; set; }
    public double? MaxY { get; set; }
    public double? MaxZ { get; set; }
}

/// <summary>One of the user's preset files, as OrcaSlicer saved it.</summary>
public class SlicerUploadPresetDto
{
    /// <summary><c>process</c>, <c>filament</c> or <c>machine</c> (OrcaSlicer's folder names).</summary>
    public string? Kind { get; set; }

    /// <summary>The preset name: the file name without <c>.json</c>.</summary>
    public string? Name { get; set; }

    /// <summary>The file's contents, as a string. A file that is not a JSON object is ignored.</summary>
    public string? Json { get; set; }
}

/// <summary>The multipart body of <c>POST /api/Slicer/uploads</c>, for the OpenAPI document only.</summary>
public class SlicerUploadForm
{
    /// <summary>The G-code, gzip-compressed UTF-8 text: the whole file, or its first and last 2,000,000 bytes joined by a newline.</summary>
    public IFormFile? Gcode { get; set; }

    /// <summary>A JSON object; see <see cref="SlicerUploadMetadataDto"/>.</summary>
    public string? Metadata { get; set; }
}
