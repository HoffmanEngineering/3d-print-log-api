using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using PrintLogApi.Models.Smtp;

namespace PrintLogApi.Services
{
    public class SmtpEmailSender : IEmailSender
    {
        private readonly SmtpEmailSenderOptions _options;

        public SmtpEmailSender(IOptions<SmtpEmailSenderOptions> options)
        {
            _options = options.Value;
        }

        public async Task SendEmailAsync(string email, string subject, string message)
        {
            var mimeMessage = new MimeMessage();
            // Null-forgiven: an unconfigured sender address already threw here before nullable
            // analysis was enabled, and it fails closed either way.
            mimeMessage.From.Add(new MailboxAddress(_options.SenderName, _options.SenderEmail!));
            mimeMessage.To.Add(new MailboxAddress(string.Empty, email));
            mimeMessage.Subject = subject;

            mimeMessage.Body = new BodyBuilder
            {
                HtmlBody = message,
                TextBody = message
            }.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(_options.Host, _options.Port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_options.Username, _options.Password);
            await client.SendAsync(mimeMessage);
            await client.DisconnectAsync(true);
        }
    }
}
