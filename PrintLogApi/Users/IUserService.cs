using System.Threading.Tasks;
using PrintLogApi.Models;

namespace PrintLogApi.Users
{
    public interface IUserService
    {
        Task<User> CreateUserFromAuthId(string authUserId);
        User? GetLocalUserByAuthUserId(string authUserId);
        Task<long> GetLocalUserIdByAuthUserId(string authUserId);
        Task MarkUserAsDeactivated(long userId);
        Task ReactivateUser(long userId);
    }
}