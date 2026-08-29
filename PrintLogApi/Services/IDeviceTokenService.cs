using PrintLogApi.Models;

namespace PrintLogApi.Services;

public interface IDeviceTokenService
{
    /// <summary>
    /// Upserts a device registration, keyed on the token. FCM tokens are device-scoped and
    /// survive logout, so a token already owned by another user is reassigned to the
    /// registering user — otherwise a handed-down phone keeps receiving the previous
    /// owner's notifications. Reassignment is logged: it is legitimate, but it is also the
    /// signature of a token-hijack attempt.
    /// </summary>
    Task RegisterDevice(long userId, string token, DevicePlatform platform, string? appVersion);

    /// <summary>Removes a token, but only if it belongs to the given user.</summary>
    Task<bool> RemoveDevice(long userId, string token);

    Task<IReadOnlyList<string>> GetTokensForUser(long userId);

    /// <summary>Deletes tokens FCM reported as permanently gone. Not user-scoped.</summary>
    Task PruneTokens(IEnumerable<string> tokens);
}
