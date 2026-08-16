namespace PrintLogApi.Models.DTOs.Octoprint
{
    public class OctoprintWebhookMetaDto
    {
        /// <summary>
        /// SHA1 Hash of the file.
        /// </summary>
        public string? Hash { get; set; }

        public OctoprintWebhookMetaAnalysisDto? Analysis { get; set; }
    }
}
