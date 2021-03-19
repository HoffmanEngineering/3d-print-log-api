using System.Threading.Tasks;

namespace PrintLogApi.Services
{
    public interface IAuth0Service
    {
        Task DeleteUser(string oauthUserId);
        Task<string> GetManagementApiBearerToken();
        Task GetUser(string oauthUserId);
    }
}