namespace PrintLogApi.Models.DTOs.Octoprint;

/// <summary>
/// The event's payload from OctoPrint, sent as the JSON-encoded <c>Extra</c> form field.
/// </summary>
public class OctoprintWebhookExtraDto
{
    /// <summary>
    /// The file name. <c>example.gcode</c> marks the plugin's test webhook, which only checks the
    /// printer and logs nothing.
    /// </summary>
    public string? Name { get; set; }
    public string? Path { get; set; }

    /// <summary>
    /// Elapsed print time in seconds, on <c>Print Done</c> and <c>Print Failed</c>.
    /// </summary>
    public double? Time { get; set; }

}
