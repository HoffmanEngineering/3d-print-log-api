using System.Globalization;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Email.Transport;

/// <summary>
/// Sends through Amazon SES v2 using simple content plus custom headers (no raw MIME). The SDK's
/// own retries are off: every retry decision is made by the dispatcher from the classified
/// failure, because a retry the SDK makes after a timeout can deliver the message twice.
/// </summary>
public sealed class SesEmailTransport : IEmailTransport, IDisposable
{
    private const string Utf8 = "UTF-8";

    private readonly EmailOptions _options;
    private readonly Lazy<AmazonSimpleEmailServiceV2Client> _client;

    public SesEmailTransport(IOptions<EmailOptions> options)
    {
        _options = options.Value;

        // Lazy so a host with email off never needs credentials or builds a client.
        _client = new Lazy<AmazonSimpleEmailServiceV2Client>(() => new AmazonSimpleEmailServiceV2Client(
            new BasicAWSCredentials(_options.Ses.AccessKeyId, _options.Ses.SecretAccessKey),
            new AmazonSimpleEmailServiceV2Config
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(_options.Ses.Region),
                MaxErrorRetry = 0,
            }));
    }

    public async Task<string> SendAsync(EmailMessage message, CancellationToken ct)
    {
        try
        {
            var response = await _client.Value.SendEmailAsync(BuildRequest(message, _options), ct);
            return response.MessageId;
        }
        catch (Exception ex)
        {
            throw new EmailSendException(SesExceptionClassifier.Classify(ex), ex);
        }
    }

    internal static SendEmailRequest BuildRequest(EmailMessage message, EmailOptions options) => new()
    {
        FromEmailAddress = $"{options.FromName} <{options.FromAddress}>",
        ReplyToAddresses = [options.ReplyTo],
        Destination = new Destination { ToAddresses = [message.ToAddress] },
        ConfigurationSetName = string.IsNullOrWhiteSpace(options.Ses.ConfigurationSet) ? null : options.Ses.ConfigurationSet,
        Content = new EmailContent
        {
            Simple = new Message
            {
                Subject = new Content { Data = message.Subject, Charset = Utf8 },
                Body = new Body
                {
                    Html = new Content { Data = message.Html, Charset = Utf8 },
                    Text = new Content { Data = message.Text, Charset = Utf8 },
                },
                Headers = [.. message.Headers.Select(h => new MessageHeader { Name = h.Key, Value = h.Value })],
            },
        },
        EmailTags =
        [
            new MessageTag { Name = "campaign", Value = message.Campaign },
            new MessageTag { Name = "outbox_id", Value = message.OutboxId.ToString(CultureInfo.InvariantCulture) },
        ],
    };

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}
