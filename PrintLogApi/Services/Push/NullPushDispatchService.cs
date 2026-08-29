using PrintLogApi.Models;

namespace PrintLogApi.Services.Push;

/// <summary>Placeholder registration until the real dispatcher is supplied.</summary>
public class NullPushDispatchService : IPushDispatchService
{
    public Task DispatchForNotification(Notification notification, CancellationToken ct = default)
        => Task.CompletedTask;
}
