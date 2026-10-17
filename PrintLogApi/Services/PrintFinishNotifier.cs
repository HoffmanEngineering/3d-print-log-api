using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Webhooks;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Services;

/// <summary>
/// The "print completed" and "print failed" notifications, shared by every path that finishes a
/// print: the notifier and OctoPrint events (<see cref="PrintEventService.Finished"/>) and
/// <c>POST /api/Prints/{id}/complete</c> (<see cref="PrintService.CompletePrint"/>), so the two
/// cannot drift apart again (#166). The <c>print.finished</c> and <c>print.failed</c> webhooks
/// (#235) go out from here for the same reason.
/// </summary>
public interface IPrintFinishNotifier
{
    /// <summary>
    /// Notifies the print's creator when <paramref name="print"/> has just moved from a
    /// not-finished status to a finished one: "completed" for Success, "failed" for Cancelled,
    /// Failed and PartialSuccess. Nothing is sent when the print was already finished, so a
    /// retried completion or an edit after the fact stays quiet. Call it after the new status is
    /// saved. A notification that cannot be created is logged, never thrown: the finish itself
    /// has already been recorded.
    /// </summary>
    Task NotifyIfJustFinished(Print print, PrintStatus previousStatus);
}

public class PrintFinishNotifier(
    INotificationService notificationService,
    IWebhookDispatcher webhookDispatcher,
    ILogger<PrintFinishNotifier> logger) : IPrintFinishNotifier
{
    public async Task NotifyIfJustFinished(Print print, PrintStatus previousStatus)
    {
        if (CompletePrintDto.IsFinished(previousStatus) || !CompletePrintDto.IsFinished(print.Status))
        {
            return;
        }

        // Queued first and independently: a failed in-app notification must not cost the webhook.
        webhookDispatcher.Enqueue(
            print.Status == PrintStatus.Success ? WebhookEventTypes.PrintFinished : WebhookEventTypes.PrintFailed,
            print.CreatedById, print.Id);

        try
        {
            if (print.Status == PrintStatus.Success)
            {
                await notificationService.CreatePrintCompletedNotification(print.CreatedById, print.Id, print.Title);
            }
            else
            {
                await notificationService.CreatePrintFailedNotification(print.CreatedById, print.Id, print.Title);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not create the finish notification for print {PrintId} ({Status}).", print.Id, print.Status);
        }
    }
}
