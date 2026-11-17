namespace PrintLogApi.Models.DTOs.Octoprint;

/// <summary>
/// The file's metadata, sent as the JSON-encoded <c>Meta</c> form field.
/// </summary>
public class OctoprintWebhookMetaDto
{
    /// <summary>
    /// SHA1 Hash of the file.
    /// </summary>
    public string? Hash { get; set; }

    public OctoprintWebhookMetaAnalysisDto? Analysis { get; set; }
}
