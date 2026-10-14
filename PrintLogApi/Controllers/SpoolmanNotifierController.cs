using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PrintLogApi.Authentication.Spoolman;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Moonraker;
using PrintLogApi.Services.Moonraker;
using PrintLogApi.Services.Spoolman;

namespace PrintLogApi.Controllers;

/// <summary>
/// Moonraker's notifier events over the printer's Spoolman credential, so the one block pasted
/// into <c>moonraker.conf</c> both powers the Spoolman integration and logs prints. The printer
/// is the credential's, never the body's. Errors are Spoolman's <c>{"message"}</c>, like the rest
/// of <c>/spoolman</c>.
/// </summary>
[Route("spoolman")]
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Authorize(Policy = SpoolmanAuthentication.Policy)]
[EnableRateLimiting(SpoolmanController.RateLimitPolicy)]
public class SpoolmanNotifierController(IMoonrakerNotifierHandler handler) : ControllerBase
{
    [HttpPost("notifier")]
    public async Task<IActionResult> Notifier()
    {
        var userId = User.GetUserId()
            ?? throw new InvalidOperationException("The Spoolman policy guarantees an authenticated user.");

        if (!long.TryParse(User.FindFirst(SpoolmanAuthentication.PrinterIdClaim)?.Value, out var printerId))
        {
            return Message(StatusCodes.Status400BadRequest, "This connection isn't linked to a printer.");
        }

        PrintEventMessageDto? message;
        try
        {
            using var reader = new StreamReader(Request.Body);
            var envelope = JsonSerializer.Deserialize<PrintEventDto>(
                await reader.ReadToEndAsync(HttpContext.RequestAborted), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            message = envelope?.Message is { } inner ? JsonSerializer.Deserialize<PrintEventMessageDto>(inner) : null;
        }
        catch (JsonException)
        {
            message = null;
        }

        if (message is null)
        {
            return Message(StatusCodes.Status400BadRequest, "The request body is not valid JSON.");
        }

        message.PrinterId = printerId;
        await handler.HandleAsync(userId, message);
        return Content("{}", "application/json");
    }

    private ContentResult Message(int status, string message) => new()
    {
        Content = JsonSerializer.Serialize(new SpoolmanMessage(message), SpoolmanJsonContext.Wire.SpoolmanMessage),
        ContentType = "application/json",
        StatusCode = status,
    };
}
