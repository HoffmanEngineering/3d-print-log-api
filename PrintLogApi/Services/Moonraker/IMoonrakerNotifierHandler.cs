using PrintLogApi.Models.DTOs.Moonraker;

namespace PrintLogApi.Services.Moonraker;

/// <summary>
/// Handles one Moonraker notifier event for a user, whichever credential delivered it.
/// </summary>
public interface IMoonrakerNotifierHandler
{
    /// <summary>
    /// Drops the event when a live bridge is logging the printer, otherwise starts or finishes the
    /// print. <see cref="PrintEventMessageDto.PrinterId"/> is the printer the event is for.
    /// </summary>
    Task HandleAsync(long userId, PrintEventMessageDto message);
}
