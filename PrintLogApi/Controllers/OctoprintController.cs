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
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Octoprint;
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

        public OctoprintController(PrintLogContext context, IMapper mapper, TelemetryClient telemetry, ILogger<OctoprintController> logger)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
            _logger = logger;
        }

        // POST: api/Prints
        [HttpPost]
        public async Task<ActionResult> Webhook([FromForm] OctoprintWebhookDto data)
        {
            this._logger.LogInformation("Webhook Recieved:");


            //HttpContext.Request.EnableBuffering();

            //HttpContext.Request.Body.Seek(0, SeekOrigin.Begin);
            //Request.Body.Position = 0;

            //using (StreamReader stream = new StreamReader(HttpContext.Request.Body))
            //{
            //    string body = await stream.ReadToEndAsync();
            //    // body = "param=somevalue&param2=someothervalue"
            //    this._logger.LogInformation(body);
            //}
            //this._logger.LogInformation(Request.Content.ReadAsStringAsync());


            //foreach (var key in data.Keys)
            //{
            //    this._logger.LogInformation(key.ToString() + ": " + data[key]);
            //}

            int testUserId = 1;

            if (data.Topic == "Print Started")
            {
                await HandlePrintStarted(data, testUserId);
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

        private async Task HandlePrintStarted(OctoprintWebhookDto data, int userId)
        {
            var newPrint = new Print
            {
                Status = PrintStatus.Printing,
                CreatedById = userId,
                UpdatedById = userId,
                Title = data.Job.File.Name,
                EstimatedPrintTimeInSeconds = (int)Math.Round(data.Job.AveragePrintTime ?? data.Job.EstimatedPrintTime ?? 0.0)
            };

            // TODO: Figure out last printer. Eventually use DeviceIdentifier, but...
            var lastSelectedPrinterUserSettingTypeId = 2;
            var setting = await _context.UserSettings.Where(u => u.UserId == userId && u.UserSettingTypeId == lastSelectedPrinterUserSettingTypeId).FirstOrDefaultAsync();
            var lastPrinterIdValue = setting.Value;

            if (int.TryParse(lastPrinterIdValue, out int printerId))
            {
                newPrint.PrinterId = printerId;
            } else
            {
                // Printer isn't found, so... shrug
                throw new Exception();
            }

            // TODO: Figure out last printer. Eventually use DeviceIdentifier, but...
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

            newPrint.StartDate = DateTimeOffset.FromUnixTimeSeconds(data.CurrentTime);


            _context.Prints.Add(newPrint);

            await _context.SaveChangesAsync();
            
        }
    }
}
