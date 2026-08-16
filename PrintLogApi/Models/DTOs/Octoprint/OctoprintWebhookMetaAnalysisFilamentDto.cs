namespace PrintLogApi.Models.DTOs.Octoprint;

public class OctoprintWebhookMetaAnalysisFilamentDto
{
    public OctoprintWebhookFilamentUsageDto? tool0 { get; set; }
    public OctoprintWebhookFilamentUsageDto? tool1 { get; set; }
    public OctoprintWebhookFilamentUsageDto? tool2 { get; set; }
    public OctoprintWebhookFilamentUsageDto? tool3 { get; set; }
    public OctoprintWebhookFilamentUsageDto? tool4 { get; set; }
}
