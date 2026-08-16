using System.Threading.Tasks;

namespace PrintLogApi.Services
{
    public interface IEmailSender
    {
        Task SendEmailAsync(string email, string subject, string message);
    }
}
