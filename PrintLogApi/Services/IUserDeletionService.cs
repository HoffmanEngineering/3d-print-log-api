using PrintLogApi.Models;

namespace PrintLogApi.Services;

public interface IUserDeletionService
{
    Task DeleteAllDataForUser(User user);
    /// <summary>
    /// Delete data for any users who's deactivation date is before the pending timeout period.
    /// </summary>
    /// <returns></returns>
    Task DeletePendingDeactivatedUsers();
}
