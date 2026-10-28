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
    /// True when <see cref="Finished"/> would find a Printing print to close for this event.
    /// Read-only.
    /// </summary>
    Task<bool> HasPrintToFinish(PrintFinishedEvent finished);

    /// <summary>
    /// True when the printer has a live bridge connection of <paramref name="kind"/>, so an event
    /// the printer's host software sent on its own (the Moonraker notifier for <c>moonraker</c>,
    /// the OctoPrint Webhooks plugin for <c>octoprint</c>) must be dropped: the bridge logs the
    /// same job. The drop is counted on the connection.
    /// </summary>
    Task<bool> DropNotifierEventForBridge(long userId, long printerId, string kind);

    /// <summary>
    /// True when the printer has a live Moonraker bridge connection, as for
    /// <see cref="DropNotifierEventForBridge"/>, but read-only: nothing is counted. Spoolman
    /// <c>/use</c> records nothing while this is true, even on a notifier print that started
    /// before the bridge went live and whose finish will still close it (#295).
    /// </summary>
    Task<bool> IsBridgeLive(long userId, long printerId);
}
