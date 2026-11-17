namespace PrintLogApi.Models.DTOs.Octoprint;

/// <summary>
/// OctoPrint's current job, sent as the JSON-encoded <c>Job</c> form field.
/// </summary>
public class OctoprintWebhookJobDto
{
    public OctoprintWebhookJobFileDto? File { get; set; }

    /// <summary>OctoPrint's estimate from analysing the file, in seconds.</summary>
    public double? EstimatedPrintTime { get; set; }

    /// <summary>The average of previous prints of this file, in seconds.</summary>
    public double? AveragePrintTime { get; set; }

    /// <summary>The previous print of this file, in seconds.</summary>
    public double? LastPrintTime { get; set; }

    public OctoprintWebhookMetaAnalysisFilamentDto? Filament { get; set; }

}

public class OctoprintWebhookJobFileDto
{
    public string? Name { get; set; }
}
