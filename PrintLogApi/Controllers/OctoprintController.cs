using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using BrunoZell.ModelBinding;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Octoprint;
using PrintLogApi.Services;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OctoprintController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;
        private readonly ILogger _logger;
        private readonly IUserApiKeyService _userApiKeyService;

        public OctoprintController(PrintLogContext context, IMapper mapper, TelemetryClient telemetry, ILogger<OctoprintController> logger, IUserApiKeyService userApiKeyService)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
            _logger = logger;
            _userApiKeyService = userApiKeyService;
        }

        // POST: api/Prints
        [HttpPost]
        public async Task<ActionResult> Webhook([FromForm] OctoprintWebhookDto data)
        {
            this._logger.LogInformation("Webhook Recieved:");

            var apiKey = data.ApiSecret;

            if (apiKey is null)
            {
                return Unauthorized("No API Secret Sent");
            }

            long userId;
            try
            {
                userId = await _userApiKeyService.GetUserIdByApiKey(apiKey);
            } catch (ApiKeyIsNotValidException)
            {
                return Unauthorized("Invalid API Key");
            }


            switch (data.Topic)
            {
                case "Print Started":
                    await HandlePrintStarted(data, userId);
                    break;
                case "Print Failed":
                case "Error":
                    await HandlePrintFailed(data, userId);
                    break;
                case "Print Done":
                    await HandlePrintCompleted(data, userId);
                    break;
            }

            return Ok(data);
            
            //var userId = User.GetUserId();

            //if (!userId.HasValue)
            //{
            //    return Unauthorized();
            //}

            //try
            //{
            //    var newPrint = await _printService.AddPrint(print, userId.Value);
            //    _telemetry.TrackEvent("PrintAdded");

            //    return CreatedAtAction("GetPrint", new { id = newPrint.Id }, _mapper.Map<PrintDetailDTO>(newPrint));
            //}
            //catch (UserCannotAccessPrinterException)
            //{
            //    return BadRequest("Selected printer does not belong to currently logged in user.");
            //}
            //catch (UserCannotAccessFilamentException)
            //{
            //    return BadRequest("Selected filament does not belong to currently logged in user.");
            //}

        }

        private async Task HandlePrintStarted(OctoprintWebhookDto data, long userId)
        {
            var newPrint = new Print
            {
                Status = PrintStatus.Printing,
                CreatedById = userId,
                UpdatedById = userId,
                Title = data.Job.File.Name,
                EstimatedPrintTimeInSeconds = (int)Math.Round(data.Job.AveragePrintTime ?? data.Job.EstimatedPrintTime ?? 0.0)
            };

            if (int.TryParse(data.DeviceIdentifier, out int printerId))
            {
                // Check the Printer to make sure the user has access to it.
                var printer = await _context.Printers.FindAsync(data.DeviceIdentifier);
                newPrint.Printer = printer;

                // Check if the user had access to that printer!
                if (userId != printer.UserId)
                {
                    throw new UserCannotAccessPrinterException();
                }
            } else
            {
                throw new Exception();
            }

            

            // Determine the Allow Comments settings
            var lastSelectedAllowCommentsUserSettingTypeId = 3;
            var setting = await _context.UserSettings.Where(u => u.UserId == userId && u.UserSettingTypeId == lastSelectedAllowCommentsUserSettingTypeId).FirstOrDefaultAsync();
            var lastSelectedAllowCommentsValue = setting.Value;

            if (bool.TryParse(lastSelectedAllowCommentsValue, out bool allowComments))
            {
                newPrint.AllowComments = allowComments;
            } else
            {
                // Printer isn't found, so... shrug
                newPrint.AllowComments = false;
            }

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

            // Work with File Hash
            newPrint.FileHash = StringToByteArray(data.Meta.Hash);

            newPrint.StartDate = DateTimeOffset.FromUnixTimeSeconds(data.CurrentTime);


            _context.Prints.Add(newPrint);

            await _context.SaveChangesAsync();
            
        }

        private async Task HandlePrintFailed(OctoprintWebhookDto data, long userId)
        {
            // Find a print thats Printing with that same hash.
            var hash = StringToByteArray(data.Meta.Hash);

            var print = await _context.Prints.Where(p => p.CreatedById == userId && p.Status == PrintStatus.Printing && p.FileHash == hash).FirstOrDefaultAsync();

            if (print == null)
            {
                return;
            }

            print.Status = PrintStatus.Failed;

            print.PrintTimeInSeconds = (int)Math.Round(data.Extra.Time ?? 0.0);
            print.UpdatedById = userId;
            _context.Entry(print).State = EntityState.Modified;


            await _context.SaveChangesAsync();

        }

        private async Task HandlePrintCompleted(OctoprintWebhookDto data, long userId)
        {
            // Find a print thats Printing with that same hash.
            var hash = StringToByteArray(data.Meta.Hash);

            var print = await _context.Prints.Where(p => p.CreatedById == userId && p.Status == PrintStatus.Printing && p.FileHash == hash).FirstOrDefaultAsync();

            if (print == null)
            {
                return;
            }

            print.Status = PrintStatus.Success;

            print.PrintTimeInSeconds = (int)Math.Round(data.Extra.Time ?? 0.0);
            print.UpdatedById = userId;
            _context.Entry(print).State = EntityState.Modified;


            await _context.SaveChangesAsync();

        }

        public static byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                             .Where(x => x % 2 == 0)
                             .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                             .ToArray();
        }
    }
}
