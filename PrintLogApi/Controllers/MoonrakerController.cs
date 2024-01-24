using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
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

        public MoonrakerController(PrintLogContext context,
                                   TelemetryClient telemetry,
                                   ILogger<MoonrakerController> logger,
                                   IPrintService printService)
        {
            _context = context;
            _telemetry = telemetry;
            _logger = logger;
            _printService = printService;

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

            var printEventDto = JsonSerializer.Deserialize<PrintEventDto>(decodedString, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var dto = JsonSerializer.Deserialize<PrintEventMessageDto>(printEventDto.Message);

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
                        var properties = new Dictionary<string, string> { { "event", dto.EventName } };
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

            var splitFilename = filenameWithoutExtension.Replace('_', ' ').Replace('-', ' ');
            var textInfo = new CultureInfo("en-US", false).TextInfo;
            var title = textInfo.ToTitleCase(splitFilename);

            var newPrint = new Print
            {
                Status = PrintStatus.Printing,
                CreatedById = userId,
                UpdatedById = userId,
                Title = title[..Math.Min(title.Length, 100)] ?? "",
                EstimatedPrintTimeInSeconds = 0,
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
                newPrint.Printer = printer;

                // Check if the user had access to that printer!
                if (userId != printer.UserId)
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
                var viewStatusValue = defaultPrintViewStatusSetting.Value;

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

            var printersLoadedFilament = newPrint.Printer.LoadedFilaments ?? new List<PrinterFilament>();


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

        }




        private async Task HandlePrintFailed(PrintEventMessageDto data, long userId)
        {
            Print print = null;


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
                .Include(p => p.FilamentUsage)
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

            print.PrintTimeInSeconds = (int)Math.Round(data?.PrintDuration ?? 0.0);
            print.UpdatedById = userId;
            _context.Entry(print).State = EntityState.Modified;


            var printersLoadedFilament = print.Printer.LoadedFilaments ?? new List<PrinterFilament>();

            if (data?.FilamentUsed is not null)
            {
                var lengthInM = Math.Round(data?.FilamentUsed / 1000 ?? 0.0, 3);

                if (print.FilamentUsage.Count > 0)
                {


                    print.FilamentUsage.ElementAt(0).LengthInM = lengthInM;
                    print.FilamentUsage.ElementAt(0).Source = PrintFilament.SourceMeasurement.Length;


                } else
                {
                    print.FilamentUsage.Add(new PrintFilament
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

        }

        private async Task HandlePrintCompleted(PrintEventMessageDto data, long userId)
        {
            Print print = null;

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
                .Include(p => p.FilamentUsage)
                .ThenInclude(pf => pf.Filament)
                .Include(p => p.Printer)
                .ThenInclude(pr => pr.LoadedFilaments)
                .FirstOrDefaultAsync();
            }
            else

            if (print == null)
            {
                _logger.LogWarning("Matching print was not found.", data);
                return;
            }

            print.Status = PrintStatus.Success;

            print.PrintTimeInSeconds = (int)Math.Round(data?.TotalDuration ?? 0.0);
            print.UpdatedById = userId;
            _context.Entry(print).State = EntityState.Modified;

            var printersLoadedFilament = print.Printer.LoadedFilaments ?? new List<PrinterFilament>();

            if (data?.FilamentUsed is not null)
            {
                var lengthInM = Math.Round(data?.FilamentUsed / 1000 ?? 0.0, 3);

                if (print.FilamentUsage.Count > 0)
                {
                    print.FilamentUsage.ElementAt(0).LengthInM = lengthInM;
                    print.FilamentUsage.ElementAt(0).Source = PrintFilament.SourceMeasurement.Length;
                }
                else
                {
                    print.FilamentUsage.Add(new PrintFilament
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

        }

    }
}
