using System.Threading.Tasks;
using PrintLogApi.Models.SendGrid;

namespace PrintLogApi.Services
{
    public interface IEmailSender
    {
        SendGridEmailSenderOptions Options { get; set; }

        Task SendEmailAsync(string email, string subject, string message);
    }
}