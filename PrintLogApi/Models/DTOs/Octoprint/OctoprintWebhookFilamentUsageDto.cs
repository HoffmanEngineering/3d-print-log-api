namespace PrintLogApi.Models.DTOs.Octoprint;

/// <summary>
/// One extruder's estimated filament use, from OctoPrint's file analysis.
/// </summary>
public class OctoprintWebhookFilamentUsageDto
{
    /// <summary>Estimated length in millimeters.</summary>
    public double length { get; set; }

    public double volumn { get; set; }
}
