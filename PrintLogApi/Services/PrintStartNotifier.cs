using PrintLogApi.Models;
using PrintLogApi.Webhooks;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Services;

/// <summary>
/// The "print started" choke point (#235), the counterpart of <see cref="IPrintFinishNotifier"/>.
/// Every path that creates a print in Printing, or moves one into it, calls this after saving:
/// <see cref="PrintEventService.Started"/> (the Moonraker notifier and OctoPrint),
/// <see cref="PrintService.AddPrint"/> (<c>POST /api/Prints</c>: printlog-bridge, the slicer
/// plugins, the web app), the replay of an external pair that moves a Pending print to Printing, and
/// <see cref="PrintService.CreatePrintForMcp"/>. Manual status edits (PATCH, PUT, bulk) do not call
/// it, just as they do not notify a finish.
/// </summary>
public interface IPrintStartNotifier
{
    /// <summary>
    /// Sends <c>print.started</c> when <paramref name="print"/> is Printing and was not before:
    /// <paramref name="previousStatus"/> is null for a new print, or Pending. Nothing is sent for a
    /// replay of a print already Printing or finished. Never throws.
    /// </summary>
    void NotifyIfJustStarted(Print print, PrintStatus? previousStatus);
}

public class PrintStartNotifier(IWebhookDispatcher webhookDispatcher) : IPrintStartNotifier
{
    public void NotifyIfJustStarted(Print print, PrintStatus? previousStatus)
    {
        if (print.Status != PrintStatus.Printing || previousStatus is not (null or PrintStatus.Pending))
        {
            return;
        }
        webhookDispatcher.Enqueue(WebhookEventTypes.PrintStarted, print.CreatedById, print.Id);
    }
}
