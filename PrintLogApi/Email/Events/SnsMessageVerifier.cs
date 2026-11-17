using Amazon.SimpleNotificationService.Util;

namespace PrintLogApi.Email.Events;

/// <param name="Type">Notification, SubscriptionConfirmation or UnsubscribeConfirmation.</param>
/// <param name="Message">For a Notification, the SES event JSON.</param>
public record SnsEnvelope(string Type, string TopicArn, string? Message, string? SubscribeUrl);

public static class EmailEventsConstants
{
    /// <summary>Named HttpClient for confirming SNS subscriptions; tests swap its handler.</summary>
    public const string SnsHttpClient = "sns";
}

/// <summary>Parses an SNS HTTP(S) delivery and verifies AWS signed it. A seam so tests need no AWS key.</summary>
public interface ISnsMessageVerifier
{
    /// <returns>False for anything unparseable or not signed by SNS.</returns>
    bool TryParse(string body, out SnsEnvelope envelope);
}

public sealed class SnsMessageVerifier : ISnsMessageVerifier
{
    public bool TryParse(string body, out SnsEnvelope envelope)
    {
        envelope = default!;
        try
        {
            var message = Message.ParseMessage(body);

            // Checks that SigningCertURL is an SNS host before fetching the certificate, then
            // verifies the signature over the canonical fields.
            if (!message.IsMessageSignatureValid())
            {
                return false;
            }

            envelope = new SnsEnvelope(message.Type, message.TopicArn, message.MessageText, message.SubscribeURL);
            return true;
        }
        catch (Exception)
        {
            // Malformed JSON, an untrusted certificate host, a download failure: all "not verified".
            return false;
        }
    }
}
