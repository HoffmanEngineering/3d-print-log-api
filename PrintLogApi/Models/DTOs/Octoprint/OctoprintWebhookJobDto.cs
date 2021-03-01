namespace PrintLogApi.Models.DTOs.Octoprint
{
    public class OctoprintWebhookJobDto
    {
        public OctoprintWebhookJobFileDto File { get; set; }
        public double? EstimatedPrintTime { get; set; }
        public double? AveragePrintTime { get; set; }
        public double? LastPrintTime { get; set; }

    }

    public class OctoprintWebhookJobFileDto
    {
        public string Name { get; set; }
    }
}
