using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email;
using PrintLogApi.Email.Events;
using PrintLogApi.IntegrationTests.Email;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class EmailEventsControllerTests : IClassFixture<EmailEventsControllerTests.WebhookFactory>
{
    public const string TopicArn = "arn:aws:sns:us-east-1:123456789012:printlog-email-events";

    /// <summary>
    /// Trusts any envelope whose "Signature" is "valid", so tests can post recorded shapes
    /// without AWS's signing key. The real verifier has its own tests.
    /// </summary>
    public sealed class FakeSnsMessageVerifier : ISnsMessageVerifier
    {
        public bool TryParse(string body, out SnsEnvelope envelope)
        {
            envelope = default!;
            try
            {
                var json = JsonNode.Parse(body)!.AsObject();
                if ((string?)json["Signature"] != "valid")
                {
                    return false;
                }

                envelope = new SnsEnvelope((string)json["Type"]!, (string)json["TopicArn"]!, (string?)json["Message"], (string?)json["SubscribeURL"]);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    public sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    public sealed class WebhookFactory : CustomWebApplicationFactory
    {
        public RecordingHandler Sns { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Email:Ses:EventsTopicArn"] = TopicArn }));
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ISnsMessageVerifier, FakeSnsMessageVerifier>();
                services.AddHttpClient(EmailEventsConstants.SnsHttpClient).ConfigurePrimaryHttpMessageHandler(() => Sns);
            });
        }
    }

    private readonly WebhookFactory _factory;

    public EmailEventsControllerTests(WebhookFactory factory) => _factory = factory;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Envelope(string type, string? message = null, string topic = TopicArn, string signature = "valid", string? subscribeUrl = null)
        => new JsonObject
        {
            ["Type"] = type,
            ["TopicArn"] = topic,
            ["Message"] = message,
            ["SubscribeURL"] = subscribeUrl,
            ["Signature"] = signature,
        }.ToJsonString();

    private Task<HttpResponseMessage> PostAsync(string body)
        => _factory.CreateClient().PostAsync("/api/email-events/ses", new StringContent(body, Encoding.UTF8, "text/plain"), Ct);

    [Fact]
    public async Task WrongTopic_400()
        => Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(Envelope("Notification", "{}", topic: "arn:aws:sns:us-east-1:999:other"))).StatusCode);

    [Fact]
    public async Task InvalidSignature_400()
        => Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(Envelope("Notification", "{}", signature: "forged"))).StatusCode);

    [Fact]
    public async Task SubscriptionConfirmation_VisitsSubscribeUrl()
    {
        const string url = "https://sns.us-east-1.amazonaws.com/?Action=ConfirmSubscription&TopicArn=x&Token=abc";

        var response = await PostAsync(Envelope("SubscriptionConfirmation", subscribeUrl: url));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.Sns.Requests, u => u.ToString() == url);
    }

    // Even with a valid signature, the confirmation URL must point at SNS: this endpoint will
    // not fetch an arbitrary address on a caller's behalf.
    [Fact]
    public async Task SubscriptionConfirmation_NonSnsUrl_400()
    {
        var response = await PostAsync(Envelope("SubscriptionConfirmation", subscribeUrl: "https://169.254.169.254/latest/meta-data"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_factory.Sns.Requests, u => u.Host == "169.254.169.254");
    }

    [Fact]
    public async Task Notification_Processed_200()
    {
        var email = $"bounce-{Guid.NewGuid():N}@example.com";

        var response = await PostAsync(Envelope("Notification", SesEventProcessorTests.Bounce(email)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IEmailSuppressionService>().IsSuppressedAsync(email, Ct));
    }

    // SNS retries on 5xx for about an hour; processing is idempotent, so a retry is safe.
    [Fact]
    public async Task ProcessingFailure_500()
        => Assert.Equal(HttpStatusCode.InternalServerError, (await PostAsync(Envelope("Notification", "{not json"))).StatusCode);

    [Fact]
    public async Task UnknownEventType_200()
        => Assert.Equal(HttpStatusCode.OK, (await PostAsync(Envelope("Notification", "{\"eventType\":\"Open\",\"mail\":{\"messageId\":\"m\"}}"))).StatusCode);

    [Fact]
    public async Task DuplicateBounce_OneSuppressionRow()
    {
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        var body = Envelope("Notification", SesEventProcessorTests.Bounce(email));

        await PostAsync(body);
        await PostAsync(body);

        using var scope = _factory.Services.CreateScope();
        var hash = scope.ServiceProvider.GetRequiredService<IEmailAddressHasher>().Hash(email);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<PrintLogContext>().EmailSuppressions.CountAsync(s => s.EmailHash == hash, Ct));
    }

    // The real verifier: a certificate URL that is not an SNS host is refused before anything
    // is downloaded.
    [Fact]
    public void RealVerifier_RejectsNonSnsCertificateHost()
    {
        var body = new JsonObject
        {
            ["Type"] = "Notification",
            ["MessageId"] = "id",
            ["TopicArn"] = TopicArn,
            ["Message"] = "{}",
            ["Timestamp"] = "2026-11-02T15:00:00.000Z",
            ["SignatureVersion"] = "1",
            ["Signature"] = "AAAA",
            ["SigningCertURL"] = "https://evil.example.com/SimpleNotificationService.pem",
        }.ToJsonString();

        Assert.False(new SnsMessageVerifier().TryParse(body, out _));
    }

    [Fact]
    public void RealVerifier_RejectsGarbage()
        => Assert.False(new SnsMessageVerifier().TryParse("not json at all", out _));
}
