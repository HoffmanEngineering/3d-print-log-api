using PrintLogApi.Models;

namespace PrintLogApi.Services;

public interface IUserDeletionService
{
    Task DeleteAllDataForUser(User user);

    /// <summary>
    /// Deletes every user whose deactivation is older than the grace period
    /// (<c>PendingUserDeactivationTimeInMinutes</c>), from Auth0 and then from the database. A user
    /// that fails is logged and left for the next run; the others are still deleted. Run by
    /// <see cref="UserDeletion.PendingUserDeletionJob"/>.
    /// </summary>
    Task<PendingUserDeletionResult> DeletePendingDeactivatedUsers(CancellationToken ct = default);
}

/// <summary>What one pass of <see cref="IUserDeletionService.DeletePendingDeactivatedUsers"/> did.</summary>
/// <param name="Candidates">Users past the grace period when the pass started.</param>
/// <param name="Deleted">Users removed from Auth0 and the database.</param>
/// <param name="Failed">Users left in place by an error; the next pass tries them again.</param>
public sealed record PendingUserDeletionResult(int Candidates, int Deleted, int Failed);
