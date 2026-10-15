using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Moonraker;
using PrintLogApi.OpenApi;
using PrintLogApi.Services.Moonraker;

namespace PrintLogApi.Controllers;

/// <summary>
/// Handles moonraker integration
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class MoonrakerController(
    ILogger<MoonrakerController> logger,
    IMoonrakerNotifierHandler handler) : ControllerBase
{
    /// <summary>The <c>externalSource</c> of prints the notifier logs.</summary>
    public const string NotifierSource = "moonraker-notifier";

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
    /// <response code="200">Returned if the webhook was handled successfully, echoing the decoded event.</response>
    /// <response code="400">Returned if required data is missing in the webhook (like the printerId, etc).</response>
    /// <response code="401">Returned if the user is not authenticated.</response>
    /// <response code="403">Returned if the current user cannot access the printer specified.</response>
    [HttpPost("notifier")]
    [OpenApiRequestBody(typeof(PrintEventDto))]
    [ProducesResponseType<PrintEventMessageDto>(StatusCodes.Status200OK)]
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

        await handler.HandleAsync(userId.Value, dto);

        return Ok(dto);
    }
}
