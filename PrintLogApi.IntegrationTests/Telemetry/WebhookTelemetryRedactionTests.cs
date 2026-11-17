using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using PrintLogApi.Telemetry;
using PrintLogApi.Webhooks;
using Xunit;

namespace PrintLogApi.IntegrationTests.Telemetry;

/// <summary>
/// Webhook deliveries are tracked as HTTP dependencies, and the target URL's path can be the
/// secret (a Home Assistant webhook id). Only scheme, host and port may reach telemetry (#235).
/// </summary>
public class WebhookTelemetryRedactionTests
{
    private static readonly WebhookTelemetryRedactionInitializer Initializer = new();

    private const string Url = "https://abc123.ui.nabu.casa:8443/api/webhook/SECRET-HOOK-ID?x=1";

    private static DependencyTelemetry Delivery(string detailName = "HttpRequest")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url);
        request.Headers.Add(WebhookSender.EventHeader, "print.finished");
        var dependency = new DependencyTelemetry
        {
            Type = "Http",
            Name = "POST /api/webhook/SECRET-HOOK-ID",
            Data = Url,
            Target = "abc123.ui.nabu.casa:8443",
        };
        object detail = detailName == "HttpResponse"
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request }
            : request;
        dependency.SetOperationDetail(detailName, detail);
        return dependency;
    }

    [Fact]
    public void AWebhookDelivery_KeepsOnlySchemeHostAndPort()
    {
        var dependency = Delivery();

        Initializer.Initialize(dependency);

        Assert.Equal("https://abc123.ui.nabu.casa:8443/", dependency.Data);
        Assert.Equal("POST abc123.ui.nabu.casa", dependency.Name);
        Assert.DoesNotContain("SECRET", dependency.Data);
        Assert.DoesNotContain("SECRET", dependency.Name);
    }

    [Fact]
    public void ADeliveryMarkedOnlyByItsRequestOption_IsRedactedToo()
    {
        // The header is one signal; the option the sender sets is the other, so a delivery is
        // still recognized if a handler ever strips or renames the header.
        var request = new HttpRequestMessage(HttpMethod.Post, Url);
        request.Options.Set(WebhookSender.DeliveryOption, true);
        var dependency = new DependencyTelemetry { Type = "Http", Name = "POST /api/webhook/SECRET-HOOK-ID", Data = Url };
        dependency.SetOperationDetail("HttpRequest", request);

        Initializer.Initialize(dependency);

        Assert.Equal("https://abc123.ui.nabu.casa:8443/", dependency.Data);
        Assert.DoesNotContain("SECRET", dependency.Name);
    }

    [Fact]
    public void ADeliveryKnownOnlyByItsResponse_IsRedactedToo()
    {
        var dependency = Delivery("HttpResponse");

        Initializer.Initialize(dependency);

        Assert.DoesNotContain("SECRET", dependency.Data);
        Assert.DoesNotContain("SECRET", dependency.Name);
    }

    [Fact]
    public void OtherHttpDependencies_AreUntouched()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/x/tags");
        var dependency = new DependencyTelemetry { Type = "Http", Name = "GET /repos/x/tags", Data = request.RequestUri!.ToString() };
        dependency.SetOperationDetail("HttpRequest", request);

        Initializer.Initialize(dependency);

        Assert.Equal("https://api.github.com/repos/x/tags", dependency.Data);
        Assert.Equal("GET /repos/x/tags", dependency.Name);
    }

    [Fact]
    public void ItIsRegistered()
    {
        using var factory = new CustomWebApplicationFactory();

        var initializers = factory.Services.GetServices<ITelemetryInitializer>();

        Assert.Contains(initializers, i => i is WebhookTelemetryRedactionInitializer);
    }
}
