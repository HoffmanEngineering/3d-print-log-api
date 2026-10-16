using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using PrintLogApi.Webhooks;

namespace PrintLogApi.Telemetry;

/// <summary>
/// Cuts webhook deliveries (#235) down to <c>scheme://host:port/</c> in dependency telemetry. The
/// SDK records every outbound HttpClient call with its full URL, and a webhook URL's path can be
/// the receiver's secret: a Home Assistant webhook id is the only thing that authenticates a call
/// to it. A delivery is recognized on the request the SDK attaches to the telemetry item, by the
/// <see cref="WebhookSender.DeliveryOption"/> option or the <see cref="WebhookSender.EventHeader"/>
/// header (either is enough), so no other dependency is touched.
/// </summary>
public sealed class WebhookTelemetryRedactionInitializer : ITelemetryInitializer
{
    // The operation-detail names the dependency collector uses for HttpClient calls.
    private const string RequestDetail = "HttpRequest";
    private const string ResponseDetail = "HttpResponse";

    public void Initialize(ITelemetry telemetry)
    {
        if (telemetry is not DependencyTelemetry dependency || WebhookRequest(dependency) is not { } request)
        {
            return;
        }

        var uri = request.RequestUri!;
        dependency.Data = uri.GetLeftPart(UriPartial.Authority) + "/";
        dependency.Name = $"{request.Method.Method} {uri.Host}";
    }

    private static HttpRequestMessage? WebhookRequest(DependencyTelemetry dependency)
    {
        HttpRequestMessage? request = null;
        if (dependency.TryGetOperationDetail(RequestDetail, out var detail) && detail is HttpRequestMessage fromRequest)
        {
            request = fromRequest;
        }
        else if (dependency.TryGetOperationDetail(ResponseDetail, out detail) && detail is HttpResponseMessage { RequestMessage: { } fromResponse })
        {
            request = fromResponse;
        }

        return request is { RequestUri: not null } && IsDelivery(request) ? request : null;
    }

    // Either signal is enough: the option the sender sets, or the event header.
    private static bool IsDelivery(HttpRequestMessage request) =>
        (request.Options.TryGetValue(WebhookSender.DeliveryOption, out var marked) && marked)
        || request.Headers.Contains(WebhookSender.EventHeader);
}
