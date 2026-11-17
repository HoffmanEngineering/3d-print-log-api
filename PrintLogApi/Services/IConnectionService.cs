using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Connection;

namespace PrintLogApi.Services;

public interface IConnectionService
{
    /// <summary>
    /// Registers or refreshes the caller's connection for <paramref name="instanceId"/>, replacing
    /// its fields and stamping the heartbeat. Throws UserCannotAccessPrinterException when the
    /// printer is not the caller's. <paramref name="deviceGrantId"/> is the device grant the
    /// request authenticated with, or null: the connection records it as its bridge when that
    /// grant has a bridge (printlog-bridge#103), and records none otherwise.
    /// </summary>
    Task<(Connection Connection, bool Created)> Upsert(long userId, string instanceId, PutConnectionDto dto, long? deviceGrantId = null, bool keepBridgeLink = false);

    /// <summary>The caller's connections, optionally only those bound to one printer.</summary>
    Task<IReadOnlyList<Connection>> List(long userId, long? printerId);

    /// <summary>The caller's connection for <paramref name="instanceId"/>, or null.</summary>
    Task<Connection?> Get(long userId, string instanceId);

    /// <summary>
    /// Deletes the caller's connection, keeping the prints it logged. False when there was none.
    /// </summary>
    Task<bool> Delete(long userId, string instanceId);

    /// <summary>
    /// Records that the user dismissed the notifier notice; the first dismissal's time is kept.
    /// False when the caller has no such connection.
    /// </summary>
    Task<bool> DismissNotifierNotice(long userId, string instanceId);
}
