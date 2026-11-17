using System.Net;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using PrintLogApi.Models.Smtp;

namespace PrintLogApi.Services;

public partial class SmtpEmailSender : IEmailSender
{
    private readonly SmtpEmailSenderOptions _options;

    public SmtpEmailSender(IOptions<SmtpEmailSenderOptions> options)
    {
        _options = options.Value;
    }

    public async Task SendEmailAsync(string email, string subject, string message)
    {
        // MailKit 4.17 annotated these parameters non-nullable. A missing value already failed
        // (MailKit threw ArgumentNullException on it), so this only moves the failure ahead of the
        // connection and names the setting. FeedbackService records it like any failed send.
        var host = _options.Host ?? throw MissingSetting("Host");
        var username = _options.Username ?? throw MissingSetting("Username");
        var password = _options.Password ?? throw MissingSetting("Password");

        var mimeMessage = BuildMessage(_options, email, subject, message);

        using var client = new SmtpClient();
        await client.ConnectAsync(host, _options.Port, SecureSocketOptions.StartTls);
        await client.AuthenticateAsync(username, password);
        await client.SendAsync(mimeMessage);
        await client.DisconnectAsync(true);
    }

    internal static MimeMessage BuildMessage(SmtpEmailSenderOptions options, string email, string subject, string html)
    {
        var mimeMessage = new MimeMessage();
        // Null-forgiven: an unconfigured sender address already threw here before nullable
        // analysis was enabled, and it fails closed either way.
        mimeMessage.From.Add(new MailboxAddress(options.SenderName, options.SenderEmail!));
        mimeMessage.To.Add(new MailboxAddress(string.Empty, email));
        mimeMessage.Subject = subject;

        mimeMessage.Body = new BodyBuilder
        {
            HtmlBody = html,
            // The text alternative used to be the HTML verbatim, so plain-text clients showed
            // raw tags. A rough strip is enough: this only carries the feedback form.
            TextBody = WebUtility.HtmlDecode(Tags().Replace(html, " ")).Trim(),
        }.ToMessageBody();

        return mimeMessage;
    }

    private static InvalidOperationException MissingSetting(string name) =>
        new($"ExternalProviders:Smtp:{name} is not configured.");

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
