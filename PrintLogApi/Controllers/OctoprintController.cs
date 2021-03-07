using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using Azure.Storage.Blobs;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Octoprint;
using PrintLogApi.Services;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class OctoprintController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;
        private readonly ILogger _logger;
        private readonly IUserApiKeyService _userApiKeyService;

        // TODO: Move this out of here....
        private readonly string printImageContainerName = "printimages";
        private readonly BlobContainerClient printImageContainer;

        public OctoprintController(PrintLogContext context,
                                   IMapper mapper,
                                   TelemetryClient telemetry,
                                   ILogger<OctoprintController> logger,
                                   IUserApiKeyService userApiKeyService,
                                   IConfiguration config)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
            _logger = logger;
            _userApiKeyService = userApiKeyService;

            var blobServiceClient = new BlobServiceClient(config["AZURE_STORAGE_CONNECTION_STRING"]);
            printImageContainer = blobServiceClient.GetBlobContainerClient(printImageContainerName);
        }

        
        [HttpPost]
        public async Task<ActionResult> Webhook([FromForm] OctoprintWebhookDto data)
        {
            this._logger.LogInformation("Webhook Recieved:");

            var userId = User.GetUserId();

            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            if (isTestWebhook(data))
            {
                _telemetry.TrackEvent("OctoPrint_Webhook_Test");
                string printerName;
                if (long.TryParse(data.DeviceIdentifier, out long printerId))
                {
                    // Check the Printer to make sure the user has access to it.
                    var printer = await _context.Printers.FindAsync(printerId);
                    
                    // Check if the user had access to that printer!
                    if (printer is null || userId != printer.UserId)
                    {
                        return BadRequest("Printer does not belong to current user. Please check DeviceIdentifier.");
                    }

                    printerName = printer.Name;
                }
                else
                {
                    return BadRequest("No Printer Id found in webhook's DeviceIdentifier.");
                }

                return Ok($"Webhook Connection to 3D Print Log is Good!\nPrinter is {printerName}.\nReady to start logging prints.");
            }


            switch (data.Topic)
            {
                case "Print Started":
                    _telemetry.TrackEvent("OctoPrint_Webhook_Started");
                    await HandlePrintStarted(data, userId.Value);
                    break;
                case "Print Failed":
                    _telemetry.TrackEvent("OctoPrint_Webhook_Failed");
                    await HandlePrintFailed(data, userId.Value);
                    break;
                case "Error":
                    _telemetry.TrackEvent("OctoPrint_Webhook_Error");
                    await HandlePrintFailed(data, userId.Value);
                    break;
                case "Print Done":
                    _telemetry.TrackEvent("OctoPrint_Webhook_PrintDone");
                    await HandlePrintCompleted(data, userId.Value);
                    break;
                default:
                    var properties = new Dictionary<string, string> { { "Topic", data.Topic } };
                    _telemetry.TrackEvent("OctoPrint_Webhook_Unhandled", properties);
                    break;
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
            var newPrint = new Print
            {
                Status = PrintStatus.Printing,
                CreatedById = userId,
                UpdatedById = userId,
                Title = data.Job.File.Name,
                EstimatedPrintTimeInSeconds = (int)Math.Round(data.Job.AveragePrintTime ?? data.Job.EstimatedPrintTime ?? 0.0),
                FilamentUsage = new List<PrintFilament>()
            };

            if (long.TryParse(data.DeviceIdentifier, out long printerId))
            {
                // Check the Printer to make sure the user has access to it.
                var printer = await _context.Printers.FindAsync(printerId);
                newPrint.Printer = printer;

                // Check if the user had access to that printer!
                if (userId != printer.UserId)
                {
                    throw new UserCannotAccessPrinterException();
                }
            } else
            {
                throw new Exception("Invalid Device Identifier");
            }


            try
            {
                // Determine the Allow Comments settings
                var lastSelectedAllowCommentsUserSettingTypeId = 3;
                var setting = await _context.UserSettings.Where(u => u.UserId == userId && u.UserSettingTypeId == lastSelectedAllowCommentsUserSettingTypeId).FirstOrDefaultAsync();
                var lastSelectedAllowCommentsValue = setting.Value;

                if (bool.TryParse(lastSelectedAllowCommentsValue, out bool allowComments))
                {
                    newPrint.AllowComments = allowComments;
                }
                else
                {
                    // Printer isn't found, so... shrug
                    newPrint.AllowComments = false;
                }
            } catch (Exception)
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

            // Handle Filament Usage

            if (data?.Meta?.Analysis?.filament is not null)
            {
                if (data?.Meta?.Analysis?.filament?.tool0 is not null)
                {
                    newPrint.FilamentUsage.Add(new PrintFilament
                    {
                        IsEstimatedLengthSource = true,
                        Id = Guid.Empty,
                        EstimatedLengthInM = data?.Meta?.Analysis?.filament?.tool0?.length/1000 ?? 0.0,
                        IsActualLengthSource = false,
                        Notes= ""
                    });
                }

                if (data?.Meta?.Analysis?.filament?.tool1 is not null)
                {
                    newPrint.FilamentUsage.Add(new PrintFilament
                    {
                        IsEstimatedLengthSource = true,
                        Id = Guid.Empty,
                        EstimatedLengthInM = data?.Meta?.Analysis?.filament?.tool1?.length / 1000 ?? 0.0,
                        IsActualLengthSource = false,
                        Notes = ""
                    });
                }

                if (data?.Meta?.Analysis?.filament?.tool2 is not null)
                {
                    newPrint.FilamentUsage.Add(new PrintFilament
                    {
                        IsEstimatedLengthSource = true,
                        Id = Guid.Empty,
                        EstimatedLengthInM = data?.Meta?.Analysis?.filament?.tool2?.length / 1000 ?? 0.0,
                        IsActualLengthSource = false,
                        Notes = ""
                    });
                }

                if (data?.Meta?.Analysis?.filament?.tool3 is not null)
                {
                    newPrint.FilamentUsage.Add(new PrintFilament
                    {
                        IsEstimatedLengthSource = true,
                        Id = Guid.Empty,
                        EstimatedLengthInM = data?.Meta?.Analysis?.filament?.tool3?.length / 1000 ?? 0.0,
                        IsActualLengthSource = false,
                        Notes = ""
                    });
                }

                if (data?.Meta?.Analysis?.filament?.tool4 is not null)
                {
                    newPrint.FilamentUsage.Add(new PrintFilament
                    {
                        IsEstimatedLengthSource = true,
                        Id = Guid.Empty,
                        EstimatedLengthInM = data?.Meta?.Analysis?.filament?.tool4?.length / 1000 ?? 0.0,
                        IsActualLengthSource = false,
                        Notes = ""
                    });
                }
            }

            // Work with File Hash
            newPrint.FileHash = StringToByteArray(data.Meta.Hash);

            newPrint.StartDate = DateTimeOffset.FromUnixTimeSeconds(data.CurrentTime);


            _context.Prints.Add(newPrint);


            if (data.snapshot is not null)
            {
                // Images
                var image = data.snapshot;
                var fileId = Guid.NewGuid();
                var fileName = fileId + Path.GetExtension(image.FileName);



                var blobClient = printImageContainer.GetBlobClient(fileName);

                using (var uploadFileStream = image.OpenReadStream())
                {
                    await blobClient.UploadAsync(uploadFileStream);
                };

                var file = new Models.File()
                {
                    Size = image.Length,
                    Path = $"{printImageContainerName}/{fileName}",
                    Id = fileId,
                    CreatedById = userId,
                    UpdatedById = userId,
                };
                _context.Files.Add(file);

                var printImage = new PrintImage()
                {
                    File = file,
                    CreatedById = userId,
                    UpdatedById = userId,
                    Print = newPrint,
                    IsDefault = true,
                };
                _context.PrintImages.Add(printImage);
            }


            await _context.SaveChangesAsync();
            
        }

        private async Task HandlePrintFailed(OctoprintWebhookDto data, long userId)
        {
            // Find a print thats Printing with that same hash.
            var hash = StringToByteArray(data.Meta.Hash);

            var print = await _context.Prints
                .Where(p => p.CreatedById == userId 
                                && p.Status == PrintStatus.Printing 
        
                                && p.FileHash == hash 
                                )
                .OrderByDescending(p => p.CreatedDate)
                .Include(p => p.FilamentUsage)
                .ThenInclude(pf => pf.Filament)
                .FirstOrDefaultAsync();

            if (print == null)
            {
                return;
            }

            print.Status = PrintStatus.Failed;

            print.PrintTimeInSeconds = (int)Math.Round(data.Extra.Time ?? 0.0);
            print.UpdatedById = userId;
            _context.Entry(print).State = EntityState.Modified;

            // Images
            if (data.snapshot != null)
            {
                var image = data.snapshot;
                var fileId = Guid.NewGuid();
                var fileName = fileId + Path.GetExtension(image.FileName);



                var blobClient = printImageContainer.GetBlobClient(fileName);

                using (var uploadFileStream = image.OpenReadStream())
                {
                    await blobClient.UploadAsync(uploadFileStream);
                };

                var file = new Models.File()
                {
                    Size = image.Length,
                    Path = $"{printImageContainerName}/{fileName}",
                    Id = fileId,
                    CreatedById = userId,
                    UpdatedById = userId,
                };
                _context.Files.Add(file);

                var printImage = new PrintImage()
                {
                    File = file,
                    CreatedById = userId,
                    UpdatedById = userId,
                    Print = print,
                    IsDefault = true,
                };
                _context.PrintImages.Add(printImage);


                // Set other defaults to false;
                var otherEntities = await _context.PrintImages.Where(p => p.PrintId == print.Id && p.IsDefault == true && p.FileId != fileId).ToListAsync();
                otherEntities.ForEach(p => p.IsDefault = false);
            }

            await _context.SaveChangesAsync();

        }

        private async Task HandlePrintCompleted(OctoprintWebhookDto data, long userId)
        {
            // Find a print thats Printing with that same hash.
            var hash = StringToByteArray(data.Meta.Hash);

            var print = await _context.Prints
                .Where(p => p.CreatedById == userId && p.Status == PrintStatus.Printing && p.FileHash == hash)
                .Include(p => p.FilamentUsage)
                .ThenInclude(pf => pf.Filament)
                .FirstOrDefaultAsync();

            if (print == null)
            {
                return;
            }

            print.Status = PrintStatus.Success;

            print.PrintTimeInSeconds = (int)Math.Round(data.Extra.Time ?? 0.0);
            print.UpdatedById = userId;
            _context.Entry(print).State = EntityState.Modified;

            // Images
            if (data.snapshot != null)
            {
                var image = data.snapshot;
                var fileId = Guid.NewGuid();
                var fileName = fileId + Path.GetExtension(image.FileName);



                var blobClient = printImageContainer.GetBlobClient(fileName);

                using (var uploadFileStream = image.OpenReadStream())
                {
                    await blobClient.UploadAsync(uploadFileStream);
                };

                var file = new Models.File()
                {
                    Size = image.Length,
                    Path = $"{printImageContainerName}/{fileName}",
                    Id = fileId,
                    CreatedById = userId,
                    UpdatedById = userId,
                };
                _context.Files.Add(file);

                var printImage = new PrintImage()
                {
                    File = file,
                    CreatedById = userId,
                    UpdatedById = userId,
                    Print = print,
                    IsDefault = true,
                };
                _context.PrintImages.Add(printImage);


                // Set other defaults to false;
                var otherEntities = await _context.PrintImages.Where(p => p.PrintId == print.Id && p.IsDefault == true && p.FileId != fileId).ToListAsync();
                otherEntities.ForEach(p => p.IsDefault = false);
            }

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
