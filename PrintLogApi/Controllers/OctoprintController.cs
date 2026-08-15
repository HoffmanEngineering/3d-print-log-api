#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Octoprint;
using PrintLogApi.Services;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Handles incoming Octoprint Webhooks.
    /// </summary>
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
        private readonly IPrintService _printService;
        private readonly INotificationService _notificationService;
        private readonly IBlobStorageService _blobStorageService;
        private readonly ICacheVersionService _cacheVersionService;

        private readonly string printImageContainerName = "printimages";

        public OctoprintController(PrintLogContext context,
                                   IMapper mapper,
                                   TelemetryClient telemetry,
                                   ILogger<OctoprintController> logger,
                                   IUserApiKeyService userApiKeyService,
                                   IPrintService printService,
                                   INotificationService notificationService,
                                   IBlobStorageService blobStorageService,
                                   ICacheVersionService cacheVersionService)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
            _logger = logger;
            _userApiKeyService = userApiKeyService;
            _printService = printService;
            _notificationService = notificationService;
            _blobStorageService = blobStorageService;
            _cacheVersionService = cacheVersionService;
        }


        /// <summary>
        /// Webhook endpoint for the Octoprint Webhooks plugin. Takes in webhook data and uses that to create or 
        /// update prints based on the statuses sent by Octoprint. See https://www.3dprintlog.com/docs/octoprint-webhookOctoprint 
        /// Webhook Docs for more information.
        /// </summary>
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
                        var properties = new Dictionary<string, string> { { "Topic", data.Topic! } };
                        _telemetry.TrackEvent("OctoPrint_Webhook_Unhandled", properties);
                        break;
                }
            }
            catch (Exception)
            {
                _logger.LogError("An error occurred in the Octoprint Webhook", data);
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
            // Computed here rather than inline below: a local cannot be declared inside an object
            // initializer.
            //
            // The two sources must be chosen between with the canonical rule, NOT with `??`.
            // `AveragePrintTime ?? EstimatedPrintTime` picks Average whenever it is non-null — and
            // 0.0 is non-null, so a zero average would silently discard a perfectly good
            // EstimatedPrintTime. That is the same "a stored 0 beats a real value" defect this
            // change exists to eliminate.
            //
            // Round FIRST, then test positivity: 0.3 is > 0 but rounds to 0, and a stored zero
            // estimate is worse than a null, because no fallback can recover from it.
            var octoAverage = (int)Math.Round(data?.Job?.AveragePrintTime ?? 0.0);
            var octoEstimated = (int)Math.Round(data?.Job?.EstimatedPrintTime ?? 0.0);
            var octoEstimate = PrintMetrics.Resolve(octoAverage, octoEstimated);

            var newPrint = new Print
            {
                Status = PrintStatus.Printing,
                CreatedById = userId,
                UpdatedById = userId,
                Title = data?.Job?.File?.Name!.Substring(0, Math.Min(data.Job.File.Name.Length, 100)) ?? "",
                EstimatedPrintTimeInSeconds = octoEstimate > 0 ? octoEstimate : (int?)null,
                FilamentUsage = new List<PrintFilament>(),
                FileName = data?.Job?.File?.Name ?? ""
            };

            // `data` is a [FromForm]-bound complex type, which MVC always instantiates, so it is
            // never null here. The compiler only treats it as maybe-null because the defensive
            // `data?.` chains above widen its null state. Same reasoning at every `data!` below.
            if (long.TryParse(data!.DeviceIdentifier, out long printerId))
            {
                // Check the Printer to make sure the user has access to it.
                var printer = await _context.Printers
                    .Where(p => p.Id == printerId)
                    .Include(p => p.LoadedFilaments)
                    .FirstOrDefaultAsync();
                newPrint.Printer = printer!;

                // Check if the user had access to that printer!
                // Null-forgiven: an unknown printerId already threw here before nullable analysis
                // was enabled. It still fails closed, just as a 500 rather than a clean error.
                // Turning that into an explicit not-found is a behaviour change, tracked in #39.
                if (userId != printer!.UserId)
                {
                    throw new UserCannotAccessPrinterException();
                }
            }
            else
            {
                throw new Exception("Invalid Device Identifier");
            }


            try
            {
                // Determine the Allow Comments settings
                var lastSelectedAllowCommentsUserSettingTypeId = 3;
                var setting = await _context.UserSettings.Where(u => u.UserId == userId && u.UserSettingTypeId == lastSelectedAllowCommentsUserSettingTypeId).FirstOrDefaultAsync();
                // Null-forgiven deliberately: a user with no saved default has no row here, and
                // the resulting throw is what the catch below turns into the fallback value.
                // The catch is load-bearing control flow, not defensive padding.
                var lastSelectedAllowCommentsValue = setting!.Value;

                if (bool.TryParse(lastSelectedAllowCommentsValue, out bool allowComments))
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
                // Null-forgiven deliberately — see the preceding try block; the catch turns the
                // missing-row throw into the Private fallback.
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

            // Handle Filament Usage

            if (data?.Meta?.Analysis?.filament is not null)
            {
                // Provably non-null: Printer is assigned from the query above, and the access
                // check that follows it already dereferenced the same instance, so a null would
                // have thrown before reaching this line.
                var printersLoadedFilament = newPrint.Printer!.LoadedFilaments ?? new List<PrinterFilament>();

                if (data?.Meta?.Analysis?.filament?.tool0 is not null)
                {
                    newPrint.FilamentUsage.Add(new PrintFilament
                    {
                        EstimatedSource = PrintFilament.SourceMeasurement.Length,
                        Id = Guid.Empty,
                        FilamentId = printersLoadedFilament.ElementAtOrDefault(0)?.FilamentId ?? null,
                        EstimatedLengthInM = Math.Round(data?.Meta?.Analysis?.filament?.tool0?.length / 1000 ?? 0.0, 3),
                        Source = PrintFilament.SourceMeasurement.Weight,
                        Notes = ""
                    });
                }

                if (data?.Meta?.Analysis?.filament?.tool1 is not null)
                {
                    newPrint.FilamentUsage.Add(new PrintFilament
                    {
                        EstimatedSource = PrintFilament.SourceMeasurement.Length,
                        Id = Guid.Empty,
                        FilamentId = printersLoadedFilament.ElementAtOrDefault(1)?.FilamentId ?? null,
                        EstimatedLengthInM = Math.Round(data?.Meta?.Analysis?.filament?.tool1?.length / 1000 ?? 0.0, 3),
                        Source = PrintFilament.SourceMeasurement.Weight,
                        Notes = ""
                    });
                }

                if (data?.Meta?.Analysis?.filament?.tool2 is not null)
                {
                    newPrint.FilamentUsage.Add(new PrintFilament
                    {
                        EstimatedSource = PrintFilament.SourceMeasurement.Length,
                        Id = Guid.Empty,
                        FilamentId = printersLoadedFilament.ElementAtOrDefault(2)?.FilamentId ?? null,
                        EstimatedLengthInM = Math.Round(data?.Meta?.Analysis?.filament?.tool2?.length / 1000 ?? 0.0, 3),
                        Source = PrintFilament.SourceMeasurement.Weight,
                        Notes = ""
                    });
                }

                if (data?.Meta?.Analysis?.filament?.tool3 is not null)
                {
                    newPrint.FilamentUsage.Add(new PrintFilament
                    {
                        EstimatedSource = PrintFilament.SourceMeasurement.Length,
                        Id = Guid.Empty,
                        FilamentId = printersLoadedFilament.ElementAtOrDefault(3)?.FilamentId ?? null,
                        EstimatedLengthInM = Math.Round(data?.Meta?.Analysis?.filament?.tool3?.length / 1000 ?? 0.0, 3),
                        Source = PrintFilament.SourceMeasurement.Weight,
                        Notes = ""
                    });
                }

                if (data?.Meta?.Analysis?.filament?.tool4 is not null)
                {
                    newPrint.FilamentUsage.Add(new PrintFilament
                    {
                        EstimatedSource = PrintFilament.SourceMeasurement.Length,
                        Id = Guid.Empty,
                        FilamentId = printersLoadedFilament.ElementAtOrDefault(4)?.FilamentId ?? null,
                        EstimatedLengthInM = Math.Round(data?.Meta?.Analysis?.filament?.tool4?.length / 1000 ?? 0.0, 3),
                        Source = PrintFilament.SourceMeasurement.Weight,
                        Notes = ""
                    });
                }

                await _printService.UpdateFilamentUsageWeights(newPrint);
            }

            // Work with File Hash
            if (data?.Meta?.Hash is not null)
            {
                newPrint.FileHash = StringToByteArray(data.Meta.Hash);
            }


            newPrint.StartDate = DateTimeOffset.FromUnixTimeSeconds(data!.CurrentTime);


            _context.Prints.Add(newPrint);


            if (data.snapshot is not null)
            {
                var maxImages = await _printService.GetMaxImagesPerPrint(userId);
                // No existing images to count: this is a newly created print, so count is always 0.
                if (0 < maxImages)
                {
                    var image = data.snapshot;
                    var fileId = Guid.NewGuid();
                    var fileName = fileId + Path.GetExtension(image.FileName);

                    using (var uploadFileStream = image.OpenReadStream())
                    {
                        var uploadResult = await _blobStorageService.UploadAsync(printImageContainerName, fileName, uploadFileStream);

                        var file = new Models.File()
                        {
                            Size = image.Length,
                            Path = uploadResult.BlobPath,
                            Id = fileId,
                            CreatedById = userId,
                            UpdatedById = userId,
                        };
                        _context.Files.Add(file);

                        // DisplayOrder = 0: this is the first (and only) image for a newly created print from a webhook.
                        var printImage = new PrintImage()
                        {
                            File = file,
                            CreatedById = userId,
                            UpdatedById = userId,
                            Print = newPrint,
                            IsDefault = true,
                            DisplayOrder = 0,
                        };
                        _context.PrintImages.Add(printImage);
                    }
                }
            }


            await _context.SaveChangesAsync();

            // Webhook-created prints must invalidate the same caches the controllers do, or the
            // print list and analytics keep serving figures from before the print existed.
            _cacheVersionService.InvalidateUserCache(userId);
        }

        private async Task HandlePrintFailed(OctoprintWebhookDto data, long userId)
        {
            Print? print = null;

            // Find a print thats Printing with that same hash.
            if (data?.Meta?.Hash is not null)
            {
                var hash = StringToByteArray(data.Meta.Hash);
                print = await _context.Prints
                .Where(p => p.CreatedById == userId
                                && p.Status == PrintStatus.Printing

                                && p.FileHash == hash
                                )
                .OrderByDescending(p => p.CreatedDate)
                .Include(p => p.FilamentUsage!)
                .ThenInclude(pf => pf.Filament)
                .FirstOrDefaultAsync();
            }
            else if (data?.Job?.File?.Name is not null)
            {
                // if the hash doesn't exist, then look for the same file name?
                var fileName = data?.Job?.File?.Name;
                print = await _context.Prints
                .Where(p => p.CreatedById == userId
                                && p.Status == PrintStatus.Printing

                                && p.FileName == fileName
                                )
                .OrderByDescending(p => p.CreatedDate)
                .Include(p => p.FilamentUsage!)
                .ThenInclude(pf => pf.Filament)
                .FirstOrDefaultAsync();
            }
            else
            {
                // We have no other way of coorlating files other than filehash or name, so...
                _logger.LogWarning("Not enough information from octoprint to find matching print.", data);
                return;
            }

            if (print == null)
            {
                _logger.LogWarning("Matching print was not found.", data);
                return;
            }

            print.Status = PrintStatus.Failed;

            // Round FIRST, then test positivity: 0.3 rounds to 0, and persisting that 0 would
            // recreate the "looks recorded but isn't" row we are eliminating.
            var failedElapsed = (int)Math.Round(data!.Extra!.Time ?? 0.0);
            print.PrintTimeInSeconds = failedElapsed > 0 ? failedElapsed : (int?)null;
            print.UpdatedById = userId;
            _context.Entry(print).State = EntityState.Modified;

            // Images
            if (data.snapshot != null)
            {
                var maxImages = await _printService.GetMaxImagesPerPrint(userId);
                var existingImageCount = await _context.PrintImages.CountAsync(pi => pi.PrintId == print.Id);
                if (existingImageCount < maxImages)
                {
                    var image = data.snapshot;
                    var fileId = Guid.NewGuid();
                    var fileName = fileId + Path.GetExtension(image.FileName);

                    using (var uploadFileStream = image.OpenReadStream())
                    {
                        var uploadResult = await _blobStorageService.UploadAsync(printImageContainerName, fileName, uploadFileStream);

                        var file = new Models.File()
                        {
                            Size = image.Length,
                            Path = uploadResult.BlobPath,
                            Id = fileId,
                            CreatedById = userId,
                            UpdatedById = userId,
                        };
                        _context.Files.Add(file);

                        // Calculate next display order: the print may already have an image from the "Started" webhook.
                        var maxDisplayOrder = await _context.PrintImages
                            .Where(pi => pi.PrintId == print.Id)
                            .MaxAsync(pi => (int?)pi.DisplayOrder) ?? -1;

                        var printImage = new PrintImage()
                        {
                            File = file,
                            CreatedById = userId,
                            UpdatedById = userId,
                            Print = print,
                            IsDefault = true,
                            DisplayOrder = maxDisplayOrder + 1,
                        };
                        _context.PrintImages.Add(printImage);


                        // Set other defaults to false;
                        var otherEntities = await _context.PrintImages.Where(p => p.PrintId == print.Id && p.IsDefault == true && p.FileId != fileId).ToListAsync();
                        otherEntities.ForEach(p => p.IsDefault = false);
                    }
                }
            }

            await _context.SaveChangesAsync();
            _cacheVersionService.InvalidateUserCache(userId);

            // Send notification for print failure
            await _notificationService.CreatePrintFailedNotification(userId, print.Id, print.Title);

        }

        private async Task HandlePrintCompleted(OctoprintWebhookDto data, long userId)
        {
            Print? print = null;

            // Find a print thats Printing with that same hash.
            if (data?.Meta?.Hash is not null)
            {
                var hash = StringToByteArray(data.Meta.Hash);
                print = await _context.Prints
                .Where(p => p.CreatedById == userId
                                && p.Status == PrintStatus.Printing

                                && p.FileHash == hash
                                )
                .OrderByDescending(p => p.CreatedDate)
                .Include(p => p.FilamentUsage!)
                .ThenInclude(pf => pf.Filament)
                .FirstOrDefaultAsync();
            }
            else if (data?.Job?.File?.Name is not null)
            {
                // if the hash doesn't exist, then look for the same file name?
                var fileName = data?.Job?.File?.Name;
                print = await _context.Prints
                .Where(p => p.CreatedById == userId
                                && p.Status == PrintStatus.Printing

                                && p.FileName == fileName
                                )
                .OrderByDescending(p => p.CreatedDate)
                .Include(p => p.FilamentUsage!)
                .ThenInclude(pf => pf.Filament)
                .FirstOrDefaultAsync();
            }
            else
            {
                // We have no other way of coorlating files other than filehash or name, so...
                _logger.LogWarning("Not enough information from octoprint to find matching print.", data);
                return;
            }

            if (print == null)
            {
                _logger.LogWarning("Matching print was not found.", data);
                return;
            }

            print.Status = PrintStatus.Success;

            // Round FIRST, then test positivity — see HandlePrintFailed.
            var successElapsed = (int)Math.Round(data!.Extra!.Time ?? 0.0);
            print.PrintTimeInSeconds = successElapsed > 0 ? successElapsed : (int?)null;
            print.UpdatedById = userId;
            _context.Entry(print).State = EntityState.Modified;

            // Images
            if (data.snapshot != null)
            {
                var maxImages = await _printService.GetMaxImagesPerPrint(userId);
                var existingImageCount = await _context.PrintImages.CountAsync(pi => pi.PrintId == print.Id);
                if (existingImageCount < maxImages)
                {
                    var image = data.snapshot;
                    var fileId = Guid.NewGuid();
                    var fileName = fileId + Path.GetExtension(image.FileName);

                    using (var uploadFileStream = image.OpenReadStream())
                    {
                        var uploadResult = await _blobStorageService.UploadAsync(printImageContainerName, fileName, uploadFileStream);

                        var file = new Models.File()
                        {
                            Size = image.Length,
                            Path = uploadResult.BlobPath,
                            Id = fileId,
                            CreatedById = userId,
                            UpdatedById = userId,
                        };
                        _context.Files.Add(file);

                        // Calculate next display order: the print may already have an image from the "Started" webhook.
                        var maxDisplayOrder = await _context.PrintImages
                            .Where(pi => pi.PrintId == print.Id)
                            .MaxAsync(pi => (int?)pi.DisplayOrder) ?? -1;

                        var printImage = new PrintImage()
                        {
                            File = file,
                            CreatedById = userId,
                            UpdatedById = userId,
                            Print = print,
                            IsDefault = true,
                            DisplayOrder = maxDisplayOrder + 1,
                        };
                        _context.PrintImages.Add(printImage);


                        // Set other defaults to false;
                        var otherEntities = await _context.PrintImages.Where(p => p.PrintId == print.Id && p.IsDefault == true && p.FileId != fileId).ToListAsync();
                        otherEntities.ForEach(p => p.IsDefault = false);
                    }
                }
            }

            await _context.SaveChangesAsync();
            _cacheVersionService.InvalidateUserCache(userId);

            // Send notification for print completion
            await _notificationService.CreatePrintCompletedNotification(userId, print.Id, print.Title);

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
