using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Octoprint;
using PrintLogApi.Services;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Controllers;

/// <summary>
/// Handles incoming Octoprint Webhooks.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class OctoprintController(
    PrintLogContext context,
    TelemetryClient telemetry,
    ILogger<OctoprintController> logger,
    IPrintEventService printEventService) : ControllerBase
{
    /// <summary>The <c>externalSource</c> of prints the webhook logs.</summary>
    public const string WebhookSource = "octoprint-webhook";

    /// <summary>
    /// Receive a print event from the OctoPrint Webhooks plugin.
    /// </summary>
    /// <remarks>
    /// Called by the OctoPrint-Webhooks plugin, not by hand. Set it up as described at
    /// https://www.3dprintlog.com/docs/octoprint-webhook, authenticating with a personal API key.
    ///
    /// The body is form data. `deviceIdentifier` must be the id of one of the caller's printers. A
    /// `Print Started` event creates a print in the Printing status. `Print Done` marks it Success,
    /// and `Print Failed` or `Error` mark it Failed.
    /// </remarks>
    /// <param name="data">The wehbook data sent by Octoprint.</param>
    /// <response code="200">Returned if the webhook was handled successfully.</response>
    /// <response code="400">Returned if required data is missing in the webhook (like the DeviceIdentifier, etc).</response>
    /// <response code="401">Returned if the user is not authenticated.</response>
    /// <response code="403">Returned if the current user cannot access the printer specified.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Webhook([FromForm] OctoprintWebhookDto data)
    {
        logger.LogInformation("Webhook Recieved:");

        var userId = User.GetUserId();

        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        if (isTestWebhook(data))
        {
            telemetry.TrackEvent("OctoPrint_Webhook_Test");
            string printerName;
            if (long.TryParse(data.DeviceIdentifier, out long printerId))
            {
                // Check the Printer to make sure the user has access to it.
                var printer = await context.Printers.FindAsync(printerId);

                // Check if the user had access to that printer!
                if (printer is null || userId != printer.UserId)
                {
                    return BadRequest("Printer does not belong to current user. Please check DeviceIdentifier.");
                }

                printerName = printer.Name!;
            }
            else
            {
                return BadRequest("No Printer Id found in webhook's DeviceIdentifier.");
            }

            return Ok($"Webhook Connection to 3D Print Log is Good!\nPrinter is {printerName}.\nReady to start logging prints.");
        }

        try
        {
            switch (data.Topic)
            {
                case "Print Started":
                    telemetry.TrackEvent("OctoPrint_Webhook_Started");
                    await HandlePrintStarted(data, userId.Value);
                    break;
                case "Print Failed":
                    telemetry.TrackEvent("OctoPrint_Webhook_Failed");
                    await HandlePrintFailed(data, userId.Value);
                    break;
                case "Error":
                    telemetry.TrackEvent("OctoPrint_Webhook_Error");
                    await HandlePrintFailed(data, userId.Value);
                    break;
                case "Print Done":
                    telemetry.TrackEvent("OctoPrint_Webhook_PrintDone");
                    await HandlePrintCompleted(data, userId.Value);
                    break;
                default:
                    var properties = new Dictionary<string, string> { { "Topic", data.Topic! } };
                    telemetry.TrackEvent("OctoPrint_Webhook_Unhandled", properties);
                    break;
            }
        }
        catch (Exception)
        {
            logger.LogError("An error occurred in the Octoprint Webhook", data);
            throw;
        }

        return Ok(data);

    }

    private bool isTestWebhook(OctoprintWebhookDto data)
    {
        if (data?.Extra?.Name == "example.gcode")
        {
            return true;
        }

        return false;
    }

    private async Task HandlePrintStarted(OctoprintWebhookDto data, long userId)
    {
        // The two sources must be chosen between with the canonical rule, NOT with `??`.
        // `AveragePrintTime ?? EstimatedPrintTime` picks Average whenever it is non-null — and
        // 0.0 is non-null, so a zero average would silently discard a perfectly good
        // EstimatedPrintTime.
        //
        // Round FIRST, then test positivity: 0.3 is > 0 but rounds to 0, and a stored zero
        // estimate is worse than a null, because no fallback can recover from it.
        var octoAverage = (int)Math.Round(data.Job?.AveragePrintTime ?? 0.0);
        var octoEstimated = (int)Math.Round(data.Job?.EstimatedPrintTime ?? 0.0);
        var octoEstimate = PrintMetrics.Resolve(octoAverage, octoEstimated);

        if (!long.TryParse(data.DeviceIdentifier, out long printerId))
        {
            throw new Exception("Invalid Device Identifier");
        }

        var fileName = data.Job?.File?.Name;
        var filament = data.Meta?.Analysis?.filament;
        var tools = new[] { filament?.tool0, filament?.tool1, filament?.tool2, filament?.tool3, filament?.tool4 };
        var usage = tools
            .Select((tool, slot) => (tool, slot))
            .Where(t => t.tool is not null)
            .Select(t => new PrintEventUsage(
                t.slot,
                EstimatedSource: PrintFilament.SourceMeasurement.Length,
                EstimatedLengthInM: Math.Round(t.tool!.length / 1000, 3),
                Source: PrintFilament.SourceMeasurement.Weight,
                LengthInM: null,
                Notes: ""))
            .ToList();

        // The job is the file on this printer started at this moment: currentTime on a start
        // event is when the job began, and a redelivery carries the same one.
        var externalId = $"{printerId}:{data.Meta?.Hash ?? fileName}:{data.CurrentTime}";

        await printEventService.Started(new PrintStartedEvent
        {
            UserId = userId,
            PrinterId = printerId,
            Source = PrintSource.OctoPrint,
            ExternalSource = WebhookSource,
            ExternalId = externalId[..Math.Min(externalId.Length, 200)],
            Title = fileName?[..Math.Min(fileName.Length, 100)] ?? "",
            FileName = fileName ?? "",
            FileHash = data.Meta?.Hash is { } hash ? StringToByteArray(hash) : null,
            StartDate = DateTimeOffset.FromUnixTimeSeconds(data.CurrentTime),
            EstimatedPrintTimeInSeconds = octoEstimate > 0 ? octoEstimate : null,
            Usage = usage,
            Snapshot = data.snapshot,
        });
    }

    private Task HandlePrintFailed(OctoprintWebhookDto data, long userId)
        => printEventService.Finished(Finished(data, userId, PrintStatus.Failed));

    private Task HandlePrintCompleted(OctoprintWebhookDto data, long userId)
        => printEventService.Finished(Finished(data, userId, PrintStatus.Success));

    private static PrintFinishedEvent Finished(OctoprintWebhookDto data, long userId, PrintStatus status)
    {
        // Round FIRST, then test positivity: 0.3 rounds to 0, and persisting that 0 would
        // recreate the "looks recorded but isn't" row.
        var elapsed = (int)Math.Round(data.Extra?.Time ?? 0.0);
        return new PrintFinishedEvent
        {
            UserId = userId,
            // Matched by file only, never by printer: the webhook has always matched this way.
            FileHash = data.Meta?.Hash is { } hash ? StringToByteArray(hash) : null,
            FileName = data.Job?.File?.Name,
            Status = status,
            PrintTimeInSeconds = elapsed > 0 ? elapsed : null,
            Snapshot = data.snapshot,
        };
    }

    public static byte[] StringToByteArray(string hex)
    {
        return Enumerable.Range(0, hex.Length)
                         .Where(x => x % 2 == 0)
                         .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                         .ToArray();
    }
}
