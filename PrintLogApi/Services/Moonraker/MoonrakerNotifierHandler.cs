using System.Globalization;
using Humanizer;
using Microsoft.ApplicationInsights;
using PrintLogApi.Controllers;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Moonraker;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Services.Moonraker;

public class MoonrakerNotifierHandler(
    TelemetryClient telemetry,
    ILogger<MoonrakerNotifierHandler> logger,
    IPrintEventService printEventService,
    TimeProvider clock) : IMoonrakerNotifierHandler
{
    private static readonly HashSet<string> PrintEvents = ["started", "cancelled", "error", "complete"];

    public async Task HandleAsync(long userId, PrintEventMessageDto message)
    {
        if (await DroppedForBridge(userId, message))
        {
            return;
        }

        try
        {
            //# started
            //# complete
            //# error
            //# cancelled
            //# paused
            //# resumed
            switch (message.EventName)
            {
                case "started":
                    telemetry.TrackEvent("Moonraker_Webhook_Started");
                    await HandlePrintStarted(message, userId);
                    break;
                case "cancelled":
                    telemetry.TrackEvent("Moonraker_Webhook_Cancelled");
                    await HandlePrintFailed(message, userId);
                    break;
                case "error":
                    telemetry.TrackEvent("Moonraker_Webhook_Error");
                    await HandlePrintFailed(message, userId);
                    break;
                case "complete":
                    telemetry.TrackEvent("Moonraker_Webhook_Completed");
                    await HandlePrintCompleted(message, userId);
                    break;
                default:
                    var properties = new Dictionary<string, string> { { "event", message.EventName! } };
                    telemetry.TrackEvent("Moonraker_Webhook_Unhandled", properties);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred in the Moonraker Webhook");
            throw;
        }
    }

    /// <summary>
    /// True, after counting the drop, when this is a print event for a printer a live Moonraker
    /// bridge is logging (#150): the bridge reports the same job, so logging it here too would
    /// record it twice. The count tells the user to remove <c>[notifier]</c>.
    /// <para>
    /// A finish is never dropped while the notifier has its own print of the job open on that
    /// printer: it started before the bridge went live (installed mid-print, or back from being
    /// stale), so the bridge may never report that job, and dropping the finish would leave the
    /// print in Printing for good. A duplicate beats a lost print (#290, #295). It is not counted.
    /// </para>
    /// </summary>
    private async Task<bool> DroppedForBridge(long userId, PrintEventMessageDto message)
    {
        if (message.PrinterId <= 0 || message.EventName is not { } eventName || !PrintEvents.Contains(eventName))
        {
            return false;
        }

        if (eventName != "started"
            && await printEventService.HasPrintToFinish(Finished(message, userId, PrintStatus.Success, 0)))
        {
            return false;
        }

        if (!await printEventService.DropNotifierEventForBridge(userId, message.PrinterId, Connection.MoonrakerKind))
        {
            return false;
        }

        telemetry.TrackEvent("Moonraker_Webhook_DroppedForBridge", new Dictionary<string, string> { { "event", eventName } });
        return true;
    }

    private async Task HandlePrintStarted(PrintEventMessageDto data, long userId)
    {
        if (data.PrinterId <= 0)
        {
            throw new Exception("Invalid PrinterId");
        }

        var filenameWithoutExtension = Path.GetFileNameWithoutExtension(data.Filename);
        var filenameWithExtension = Path.GetFileName(data.Filename) ?? "";

        var splitFilename = filenameWithoutExtension.Humanize();
        var textInfo = new CultureInfo("en-US", false).TextInfo;
        var title = textInfo.ToTitleCase(splitFilename);

        // The notifier payload carries no start time, so the job is identified by when its start
        // arrived. That makes a redelivery within the same second the one it can recognise.
        var startDate = clock.GetUtcNow();
        var externalId = $"{data.PrinterId}:{startDate.ToUnixTimeSeconds()}:{filenameWithExtension}";

        await printEventService.Started(new PrintStartedEvent
        {
            UserId = userId,
            PrinterId = data.PrinterId,
            Source = PrintSource.Moonraker,
            ExternalSource = MoonrakerController.NotifierSource,
            ExternalId = externalId[..Math.Min(externalId.Length, 200)],
            Title = title[..Math.Min(title.Length, 100)],
            FileName = filenameWithExtension,
            StartDate = startDate,
            // The start payload carries no estimate. Record its ABSENCE, not a fake zero: a 0
            // looks recorded, so no read-side fallback can ever recover from it.
            EstimatedPrintTimeInSeconds = null,
            Usage =
            [
                new PrintEventUsage(
                    Slot: 0,
                    EstimatedSource: PrintFilament.SourceMeasurement.Length,
                    EstimatedLengthInM: 0,
                    Source: PrintFilament.SourceMeasurement.Length,
                    LengthInM: 0,
                    Notes: "Added by Moonraker"),
            ],
        });
    }

    private Task HandlePrintFailed(PrintEventMessageDto data, long userId)
        => printEventService.Finished(Finished(data, userId, PrintStatus.Failed, data.PrintDuration));

    private Task HandlePrintCompleted(PrintEventMessageDto data, long userId)
        => printEventService.Finished(Finished(data, userId, PrintStatus.Success, data.TotalDuration));

    private static PrintFinishedEvent Finished(PrintEventMessageDto data, long userId, PrintStatus status, double duration)
    {
        // Round FIRST, then test positivity: 0.3 is > 0 but rounds to 0, and persisting that 0
        // would recreate the "looks recorded but isn't" row.
        var seconds = (int)Math.Round(duration);
        return new PrintFinishedEvent
        {
            UserId = userId,
            // Only the notifier's own prints on its printer (#295), as the OctoPrint webhook's
            // (#291): a finish must not close the bridge's print of the same file and leave its
            // own in Printing.
            PrinterId = data.PrinterId,
            ExternalSource = MoonrakerController.NotifierSource,
            FileName = Path.GetFileName(data.Filename),
            Status = status,
            PrintTimeInSeconds = seconds > 0 ? seconds : null,
            ActualLengthInM = Math.Round(data.FilamentUsed / 1000, 3),
        };
    }
}
