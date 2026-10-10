using System.Globalization;
using System.Text;
using System.Text.Json;
using Humanizer;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Moonraker;
using PrintLogApi.Services;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Controllers;

/// <summary>
/// Handles moonraker integration
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class MoonrakerController(
    TelemetryClient telemetry,
    ILogger<MoonrakerController> logger,
    IPrintEventService printEventService,
    TimeProvider clock) : ControllerBase
{
    /// <summary>The <c>externalSource</c> of prints the notifier logs.</summary>
    public const string NotifierSource = "moonraker-notifier";

    private static readonly HashSet<string> PrintEvents = ["started", "cancelled", "error", "complete"];

    /// <summary>
    /// Receive a print event from Moonraker (Klipper).
    /// </summary>
    /// <remarks>
    /// Called by Moonraker's notifier on a Klipper printer, not by hand. Set it up as described at
    /// https://www.3dprintlog.com/docs/klipper, authenticating with a personal API key.
    ///
    /// The body is Moonraker's JSON notification, whose `message` field carries the print event.
    /// `started` creates a print in the Printing status. `complete` marks that print Success, and
    /// `cancelled` or `error` mark it Failed.
    ///
    /// When the printer also has a live printlog-bridge connection, the bridge logs the job and
    /// these events are dropped (and counted on the connection), so each job is logged once.
    /// </remarks>
    /// <see cref="PrintEventDto"/>
    /// <see cref="PrintEventMessageDto"/>
    /// <param>The body of the Post Request contains <see cref="PrintEventDto"/></param>
    /// <response code="200">Returned if the webhook was handled successfully.</response>
    /// <response code="400">Returned if required data is missing in the webhook (like the printerId, etc).</response>
    /// <response code="401">Returned if the user is not authenticated.</response>
    /// <response code="403">Returned if the current user cannot access the printer specified.</response>
    [HttpPost("notifier")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Webhook()
    {
        logger.LogInformation("Webhook Recieved:");

        var userId = User.GetUserId();

        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var encodedJsonString = ms.ToArray();  // returns base64 encoded string JSON result

        var decodedString = Encoding.UTF8.GetString(encodedJsonString);

        // Both deserializations return null for a literal "null" payload and then throw on
        // the following dereference. Null-forgiven to keep this change annotation-only; the
        // unvalidated webhook payload is tracked in #57.
        var printEventDto = JsonSerializer.Deserialize<PrintEventDto>(decodedString, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var dto = JsonSerializer.Deserialize<PrintEventMessageDto>(printEventDto.Message!)!;

        if (dto.PrinterId > 0 && dto.EventName is { } eventName && PrintEvents.Contains(eventName)
            && await printEventService.DropNotifierEventForBridge(userId.Value, dto.PrinterId))
        {
            telemetry.TrackEvent("Moonraker_Webhook_DroppedForBridge", new Dictionary<string, string> { { "event", eventName } });
            return Ok(dto);
        }

        try
        {
            //# started
            //# complete
            //# error
            //# cancelled
            //# paused
            //# resumed
            switch (dto.EventName)
            {
                case "started":
                    telemetry.TrackEvent("Moonraker_Webhook_Started");
                    await HandlePrintStarted(dto, userId.Value);
                    break;
                case "cancelled":
                    telemetry.TrackEvent("Moonraker_Webhook_Cancelled");
                    await HandlePrintFailed(dto, userId.Value);
                    break;
                case "error":
                    telemetry.TrackEvent("Moonraker_Webhook_Error");
                    await HandlePrintFailed(dto, userId.Value);
                    break;
                case "complete":
                    telemetry.TrackEvent("Moonraker_Webhook_Completed");
                    await HandlePrintCompleted(dto, userId.Value);
                    break;
                default:
                    var properties = new Dictionary<string, string> { { "event", dto.EventName! } };
                    telemetry.TrackEvent("Moonraker_Webhook_Unhandled", properties);
                    break;
            }
        }
        catch (Exception)
        {
            logger.LogError("An error occurred in the Moonraker Webhook", dto);
            throw;
        }

        return Ok(dto);

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
            ExternalSource = NotifierSource,
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
            PrinterId = data.PrinterId,
            FileName = Path.GetFileName(data.Filename),
            Status = status,
            PrintTimeInSeconds = seconds > 0 ? seconds : null,
            ActualLengthInM = Math.Round(data.FilamentUsed / 1000, 3),
        };
    }
}
