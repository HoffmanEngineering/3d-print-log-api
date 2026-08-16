using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Humanizer;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Moonraker;
using PrintLogApi.Services;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Handles moonraker integration
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class MoonrakerController : ControllerBase
    {

        private readonly PrintLogContext _context;

        private readonly TelemetryClient _telemetry;
        private readonly ILogger _logger;
        private readonly IPrintService _printService;
        private readonly INotificationService _notificationService;
        private readonly ICacheVersionService _cacheVersionService;

        public MoonrakerController(PrintLogContext context,
                                   TelemetryClient telemetry,
                                   ILogger<MoonrakerController> logger,
                                   IPrintService printService,
                                   INotificationService notificationService,
                                   ICacheVersionService cacheVersionService)
        {
            _context = context;
            _telemetry = telemetry;
            _logger = logger;
            _printService = printService;
            _notificationService = notificationService;
            _cacheVersionService = cacheVersionService;
        }

        /// <summary>
        /// Webhook endpoint for the Moonraker. Takes in webhook data and uses that to create or 
        /// update prints based on the statuses sent by Moonraker. See https://www.3dprintlog.com/docs/klipper Moonraker 
        /// Webhook Docs for more information.
        /// </summary>
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
            _logger.LogInformation("Webhook Recieved:");

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
                        _telemetry.TrackEvent("Moonraker_Webhook_Started");
                        await HandlePrintStarted(dto, userId.Value);
                        break;
                    case "cancelled":
                        _telemetry.TrackEvent("Moonraker_Webhook_Cancelled");
                        await HandlePrintFailed(dto, userId.Value);
                        break;
                    case "error":
                        _telemetry.TrackEvent("Moonraker_Webhook_Error");
                        await HandlePrintFailed(dto, userId.Value);
                        break;
                    case "complete":
                        _telemetry.TrackEvent("Moonraker_Webhook_Completed");
                        await HandlePrintCompleted(dto, userId.Value);
                        break;
                    default:
                        var properties = new Dictionary<string, string> { { "event", dto.EventName! } };
                        _telemetry.TrackEvent("Moonraker_Webhook_Unhandled", properties);
                        break;
                }
            }
            catch (Exception)
            {
                _logger.LogError("An error occurred in the Moonraker Webhook", dto);
                throw;
            }

            return Ok(dto);

        }

        private async Task HandlePrintStarted(PrintEventMessageDto data, long userId)
        {
            var filenameWithoutExtension = Path.GetFileNameWithoutExtension(data.Filename);
            var filenameWithExtension = Path.GetFileName(data.Filename);

            // var splitFilename = filenameWithoutExtension.Replace('_', ' ').Replace('-', ' ');
            var splitFilename = filenameWithoutExtension.Humanize();
            var textInfo = new CultureInfo("en-US", false).TextInfo;
            var title = textInfo.ToTitleCase(splitFilename);

            var newPrint = new Print
            {
                Status = PrintStatus.Printing,
                CreatedById = userId,
                UpdatedById = userId,
                Title = title[..Math.Min(title.Length, 100)] ?? "",
                // The start payload carries no estimate. Record its ABSENCE, not a fake zero: a 0
                // looks recorded, so no read-side fallback can ever recover from it.
                EstimatedPrintTimeInSeconds = null,
                FilamentUsage = new List<PrintFilament>(),
                FileName = filenameWithExtension ?? ""
            };

            if (data.PrinterId > 0)
            {
                // Check the Printer to make sure the user has access to it.
                var printer = await _context.Printers
                    .Where(p => p.Id == data.PrinterId)
                    .Include(p => p.LoadedFilaments)
                    .FirstOrDefaultAsync();
                newPrint.Printer = printer!;

                // Null-forgiven: an unknown PrinterId already threw here before nullable analysis
                // was enabled. It still fails closed, just as a 500 rather than a clean error.
                // Turning that into an explicit not-found is a behaviour change, tracked in #57.
                if (userId != printer!.UserId)
                {
                    throw new UserCannotAccessPrinterException();
                }
            }
            else
            {
                throw new Exception("Invalid PrinterId");
            }


            try
            {
                // Determine the Allow Comments settings
                var lastSelectedAllowCommentsUserSettingTypeId = 3;
                var setting = await _context.UserSettings.Where(u => u.UserId == userId && u.UserSettingTypeId == lastSelectedAllowCommentsUserSettingTypeId).FirstOrDefaultAsync();
                var lastSelectedAllowCommentsValue = setting?.Value ?? "false";

                if (bool.TryParse(lastSelectedAllowCommentsValue, out var allowComments))
                {
                    newPrint.AllowComments = allowComments;
                }
                else
                {
                    // Printer isn't found, so... shrug
                    newPrint.AllowComments = false;
                }
            }
            catch (Exception)
            {
                newPrint.AllowComments = false;
            }

            try
            {
                // Determine the last view status
                var defaultViewStatus = 1;
                var defaultPrintViewStatusSetting = await _context.UserSettings.Where(u => u.UserId == userId && u.UserSettingTypeId == defaultViewStatus).FirstOrDefaultAsync();
                // Null-forgiven deliberately: a user with no saved default has no row here, and
                // the resulting throw is what the catch below turns into the Private fallback.
                // The catch is load-bearing control flow, not defensive padding.
                var viewStatusValue = defaultPrintViewStatusSetting!.Value;

                if (PrintViewStatus.TryParse(viewStatusValue, out PrintViewStatus viewStatus))
                {
                    newPrint.ViewStatus = viewStatus;
                }
                else
                {
                    // Printer isn't found, so... shrug
                    newPrint.ViewStatus = PrintViewStatus.Private;
                }
            }
            catch (Exception)
            {
                newPrint.ViewStatus = PrintViewStatus.Private;
            }

            // Provably non-null: Printer is assigned from the query above, and the access check
            // that follows it already dereferenced the same instance, so a null would have thrown
            // before reaching this line.
            var printersLoadedFilament = newPrint.Printer!.LoadedFilaments ?? new List<PrinterFilament>();


            newPrint.FilamentUsage.Add(new PrintFilament
            {
                EstimatedSource = PrintFilament.SourceMeasurement.Length,
                Id = Guid.Empty,
                FilamentId = printersLoadedFilament.ElementAtOrDefault(0)?.FilamentId ?? null,
                EstimatedLengthInM = 0,
                Source = PrintFilament.SourceMeasurement.Length,
                LengthInM = 0,
                Notes = "Added by Moonraker"
            });

            await _printService.UpdateFilamentUsageWeights(newPrint);


            newPrint.StartDate = DateTimeOffset.UtcNow;


            _ = _context.Prints.Add(newPrint);

            _ = await _context.SaveChangesAsync();

            // Webhooks are how most prints get created for automated setups. Saving straight
            // through the context skips the invalidation the controllers do, so the cached
            // print summary and analytics aggregates would keep serving pre-print figures.
            _cacheVersionService.InvalidateUserCache(userId);
        }




        private async Task HandlePrintFailed(PrintEventMessageDto data, long userId)
        {
            Print? print = null;


            var filename = Path.GetFileName(data.Filename);


            // Find a print thats Printing with that same filename and printer
            if (filename is not null)
            {

                print = await _context.Prints
                .Where(p => p.CreatedById == userId
                                && p.Status == PrintStatus.Printing

                                && p.FileName == filename
                                && p.PrinterId == data.PrinterId
                                )
                .OrderByDescending(p => p.CreatedDate)
                .Include(p => p.FilamentUsage!)
                .ThenInclude(pf => pf.Filament)
                .Include(p => p.Printer)
                .ThenInclude(pr => pr.LoadedFilaments)
                .FirstOrDefaultAsync();
            }
            else
            {
                // We have no other way of coorlating files other than filehash or name, so...
                _logger.LogWarning("Not enough information from moonraker to find matching print.", data);
                return;
            }

            if (print == null)
            {
                _logger.LogWarning("Matching print was not found.", data);
                return;
            }

            print.Status = PrintStatus.Failed;

            // Round FIRST, then test positivity: 0.3 is > 0 but rounds to 0, and persisting that 0
            // would recreate the very "looks recorded but isn't" row we are eliminating.
            var failedDuration = (int)Math.Round(data?.PrintDuration ?? 0.0);
            print.PrintTimeInSeconds = failedDuration > 0 ? failedDuration : (int?)null;
            print.UpdatedById = userId;
            _context.Entry(print).State = EntityState.Modified;


            var printersLoadedFilament = print.Printer.LoadedFilaments ?? new List<PrinterFilament>();

            if (data?.FilamentUsed is not null)
            {
                var lengthInM = Math.Round(data?.FilamentUsed / 1000 ?? 0.0, 3);

                if (print.FilamentUsage!.Count > 0)
                {


                    print.FilamentUsage!.ElementAt(0).LengthInM = lengthInM;
                    print.FilamentUsage!.ElementAt(0).Source = PrintFilament.SourceMeasurement.Length;


                }
                else
                {
                    print.FilamentUsage!.Add(new PrintFilament
                    {
                        EstimatedSource = PrintFilament.SourceMeasurement.Length,
                        Id = Guid.Empty,
                        FilamentId = printersLoadedFilament.ElementAtOrDefault(0)?.FilamentId ?? null,
                        EstimatedLengthInM = lengthInM,
                        Source = PrintFilament.SourceMeasurement.Length,
                        LengthInM = lengthInM,
                        Notes = ""
                    });
                }

                await _printService.UpdateFilamentUsageWeights(print);
            }

            _ = await _context.SaveChangesAsync();
            _cacheVersionService.InvalidateUserCache(userId);

            // Send notification for print failure
            await _notificationService.CreatePrintFailedNotification(userId, print.Id, print.Title);

        }

        private async Task HandlePrintCompleted(PrintEventMessageDto data, long userId)
        {
            Print? print = null;

            var filename = Path.GetFileName(data.Filename);

            // Find a print thats Printing with that same filename and printer
            if (filename is not null)
            {
                print = await _context.Prints
                .Where(p => p.CreatedById == userId
                                && p.Status == PrintStatus.Printing

                                && p.FileName == filename
                                && p.PrinterId == data.PrinterId
                                )
                .OrderByDescending(p => p.CreatedDate)
                .Include(p => p.FilamentUsage!)
                .ThenInclude(pf => pf.Filament)
                .Include(p => p.Printer)
                .ThenInclude(pr => pr.LoadedFilaments)
                .FirstOrDefaultAsync();
            }
            else
            {
                // We have no other way of correlating files other than filename, so...
                _logger.LogWarning("Not enough information from moonraker to find matching print.", data);
                return;
            }

            if (print == null)
            {
                _logger.LogWarning("Matching print was not found.", data);
                return;
            }

            print.Status = PrintStatus.Success;

            // Round FIRST, then test positivity — see HandlePrintFailed.
            var totalDuration = (int)Math.Round(data?.TotalDuration ?? 0.0);
            print.PrintTimeInSeconds = totalDuration > 0 ? totalDuration : (int?)null;
            print.UpdatedById = userId;
            _context.Entry(print).State = EntityState.Modified;

            var printersLoadedFilament = print.Printer.LoadedFilaments ?? new List<PrinterFilament>();

            if (data?.FilamentUsed is not null)
            {
                var lengthInM = Math.Round(data?.FilamentUsed / 1000 ?? 0.0, 3);

                if (print.FilamentUsage!.Count > 0)
                {
                    print.FilamentUsage!.ElementAt(0).LengthInM = lengthInM;
                    print.FilamentUsage!.ElementAt(0).Source = PrintFilament.SourceMeasurement.Length;
                }
                else
                {
                    print.FilamentUsage!.Add(new PrintFilament
                    {
                        EstimatedSource = PrintFilament.SourceMeasurement.Length,
                        Id = Guid.Empty,
                        FilamentId = printersLoadedFilament.ElementAtOrDefault(0)?.FilamentId ?? null,
                        EstimatedLengthInM = lengthInM,
                        Source = PrintFilament.SourceMeasurement.Length,
                        LengthInM = lengthInM,
                        Notes = ""
                    });
                }

                await _printService.UpdateFilamentUsageWeights(print);
            }

            _ = await _context.SaveChangesAsync();
            _cacheVersionService.InvalidateUserCache(userId);

            // Send notification for print completion
            await _notificationService.CreatePrintCompletedNotification(userId, print.Id, print.Title);

        }

    }
}
