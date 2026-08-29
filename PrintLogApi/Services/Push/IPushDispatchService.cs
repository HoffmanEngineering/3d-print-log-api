using PrintLogApi.Models;

namespace PrintLogApi.Services.Push;

public interface IPushDispatchService
{
    /// <summary>
    /// Sends a push for this notification if its type is pushable, the user has not opted
    /// out, and they have a registered device. Never throws.
    /// </summary>
    Task DispatchForNotification(Notification notification, CancellationToken ct = default);
}
