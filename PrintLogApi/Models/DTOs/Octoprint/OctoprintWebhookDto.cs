using BrunoZell.ModelBinding;
using Microsoft.AspNetCore.Mvc;

namespace PrintLogApi.Models.DTOs.Octoprint;

/// <summary>
/// The form the OctoPrint Webhooks plugin posts. <c>Extra</c>, <c>Job</c> and <c>Meta</c> are
/// form fields whose values are JSON documents.
/// </summary>
public class OctoprintWebhookDto
{
    /// <summary>
    /// The id of one of the caller's printers, as text.
    /// </summary>
    public string? DeviceIdentifier { get; set; }

    /// <summary>
    /// Sent by the plugin and ignored: the request authenticates with a personal API key.
    /// </summary>
    public string? ApiSecret { get; set; }

    /// <summary>
    /// The OctoPrint event: <c>Print Started</c>, <c>Print Done</c>, <c>Print Failed</c> or
    /// <c>Error</c>. Anything else is accepted and ignored.
    /// </summary>
    public string? Topic { get; set; }

    [ModelBinder(BinderType = typeof(JsonModelBinder))]
    public OctoprintWebhookExtraDto? Extra { get; set; }

    [ModelBinder(BinderType = typeof(JsonModelBinder))]
    public OctoprintWebhookJobDto? Job { get; set; }

    [ModelBinder(BinderType = typeof(JsonModelBinder))]
    public OctoprintWebhookMetaDto? Meta { get; set; }

    /// <summary>
    /// An optional webcam snapshot, attached to the print.
    /// </summary>
    public IFormFile? snapshot { get; set; }

    /// <summary>
    /// The Unix Epoch timestamp that it started.
    /// </summary>
    public long CurrentTime { get; set; }

}
