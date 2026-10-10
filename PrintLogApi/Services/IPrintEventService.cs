namespace PrintLogApi.Services;

/// <summary>
/// The one create-or-update path for prints reported by integrations. The Moonraker notifier and
/// the OctoPrint webhook translate their payloads into these events and call it.
/// </summary>
public interface IPrintEventService
{
    /// <summary>
    /// Creates the print for a started job, or returns the one an earlier delivery of the same
    /// event created. Throws UserCannotAccessPrinterException for another user's printer.
    /// </summary>
    Task<PrintStartedResult> Started(PrintStartedEvent started);

    /// <summary>
    /// Records how a job ended and notifies the user. Null when no Printing print matches.
    /// </summary>
    Task<Models.Print?> Finished(PrintFinishedEvent finished);

    /// <summary>
    /// True when the printer has a live bridge connection, so a Moonraker notifier event for it
    /// must be dropped: the bridge logs the same job. The drop is counted on the connection.
    /// </summary>
    Task<bool> DropNotifierEventForBridge(long userId, long printerId);
}
