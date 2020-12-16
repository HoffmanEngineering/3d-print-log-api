using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Azure.Storage.Blobs;
using CsvHelper;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Comments;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.SortEnums;
using static PrintLogApi.Models.Print;
using PrintLogApi.Extensions;
using PrintLogApi.Services;
using PrintLogApi.Exceptions;

namespace PrintLogApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class PrintsController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly IAuthorizationService _authorizationService;
        private readonly TelemetryClient _telemetry;
        private readonly IPrintService _printService;
        private readonly ICommentService _commentService;
        private readonly IPrintImageService _printImageService;
        private readonly string printImageContainerName = "printimages";
        private readonly BlobContainerClient printImageContainer;

        public PrintsController(
            PrintLogContext context,
            IMapper mapper,
            IConfiguration config,
            IAuthorizationService authorizationService,
            TelemetryClient telemetry,
            IPrintService printService,
            IPrintImageService printImageService,
            ICommentService commentService)
        {
            _context = context;
            _mapper = mapper;
            _authorizationService = authorizationService;
            _telemetry = telemetry;
            _printService = printService;
            _commentService = commentService;
            _printImageService = printImageService;

            var blobServiceClient = new BlobServiceClient(config["AZURE_STORAGE_CONNECTION_STRING"]);
            printImageContainer = blobServiceClient.GetBlobContainerClient(printImageContainerName);
        }

        /// <summary>
        /// Get Print Summaries for current user
        /// </summary>
        /// <returns></returns>
        [HttpGet("summary")]
        [AllowAnonymous]
        public async Task<ActionResult<PagedList<PrintSummaryDTO>>> GetPrintSummary(
            [FromQuery] PagedRequest pagingRequest,
            [FromQuery, MaxLength(50)] string searchText,
            [FromQuery] SortRequest<PrintSummarySortColumn> sortRequest,
            [FromQuery] Print.PrintStatus? filterByStatus,
            [FromQuery] long? userId)
        {

            long? currentUserId = User.GetUserId();

            if (!userId.HasValue && userId != currentUserId && !currentUserId.HasValue)
            {
                return BadRequest("User is not logged in, and summary is not filtered by a specific userId. Please log in and try again.");
            }

            return await _printService.SearchPrintSummary(pagingRequest, searchText, sortRequest, filterByStatus, userId, currentUserId);
        }

        
        /// <summary>
        /// Get Print Statistics
        /// </summary>
        /// <returns></returns>
        [HttpGet("stats")]
        [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Client, NoStore = false)]
        public async Task<ActionResult<List<PrintStatistic>>> GetPrintStats([FromQuery] DateTimeOffset fromDate, [FromQuery] DateTimeOffset toDate)
        {

            var userId = User.GetUserId();

            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var printStats = await _printService.GetPrintStatisticsForUser(userId.Value, fromDate, toDate);

            return printStats;
        }

        

        // GET: api/Prints/5
        [HttpGet("{id}")]
        [AllowAnonymous]
        public async Task<ActionResult<PrintDetailDTO>> GetPrint(long id)
        {
            var print = await _context.Prints.FindAsync(id);

            if (print == null)
            {
                return NotFound();
            }

            if (!await CanViewPrint(print))
            {
                return Forbid();
            }

            var printDetailDto = _mapper.Map<PrintDetailDTO>(print);

            printDetailDto.Comments = printDetailDto.Comments.OrderBy(c => c.CreatedDate).ToList();

            return printDetailDto;
        }

        [HttpGet("csv")]
        public async Task<IActionResult> GetAllPrintDetailsAsCsv()
        {
            long? currentUserId = User.GetUserId();

            if (!currentUserId.HasValue)
            {
                return Unauthorized();
            }

            var stream = await _printService.GeneratePrintReportAsCsvForUser(currentUserId.Value);

            return File(stream, "application/octet-stream", "PrintReports.csv");
        }


        // PUT: api/Prints/5
        [HttpPut("{id}")]
        public async Task<ActionResult<PrintDetailDTO>> PutPrint(long id, PrintDetailDTO printDTO)
        {
            if (id != printDTO.Id)
            {
                return BadRequest();
            }

            var existingPrint = await _context.Prints.FindAsync(id);

            if (existingPrint == null)
            {
                return NotFound();
            }

            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            if (userId != existingPrint.CreatedById || userId != existingPrint.Printer.UserId)
            {
                return Forbid();
            }

            try
            {
                var updatedPrint = await _printService.UpdatePrint(id, printDTO, userId.Value);

                return CreatedAtAction("GetPrint", new { id = existingPrint.Id }, _mapper.Map<PrintDetailDTO>(updatedPrint));
            } catch (UserCannotAccessPrinterException)
            {
                return BadRequest();
            } catch (DoesNotExistException)
            {
                return NotFound();
            }          


            
        }

        // PUT: api/Prints/5/status/1
        [HttpPut("{id}/status/{newStatus}")]
        public async Task<ActionResult<PrintDetailDTO>> PutPrint(long id, PrintStatus newStatus)
        {

            var existingPrint = await _context.Prints.FindAsync(id);

            if (existingPrint == null)
            {
                return NotFound();
            }

            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }
            if ( userId != existingPrint.CreatedById || userId != existingPrint.Printer.UserId)
            {
                return Forbid();
            }

            try
            {
                var updatedPrint = await _printService.UpdatePrintStatus(id, newStatus, userId.Value);
                return CreatedAtAction("GetPrint", new { id = existingPrint.Id }, _mapper.Map<PrintDetailDTO>(existingPrint));
            } catch (DoesNotExistException)
            {
                return NotFound();
            }
            
        }

        // POST: api/Prints
        [HttpPost]
        public async Task<ActionResult<PrintDetailDTO>> PostPrint(AddPrintDTO print)
        {
            var userId = User.GetUserId();

            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var newPrint = await _printService.AddPrint(print, userId.Value);

            _telemetry.TrackEvent("PrintAdded");

            return CreatedAtAction("GetPrint", new { id = newPrint.Id }, _mapper.Map<PrintDetailDTO>(newPrint));
        }

        

        // DELETE: api/Prints/5
        [HttpDelete("{id}")]
        public async Task<ActionResult<PrintDetailDTO>> DeletePrint(long id)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var existingPrint = await _context.Prints.FindAsync(id);

            if (existingPrint == null)
            {
                return NotFound();
            }


            if (userId != existingPrint.CreatedById)
            {
                return Forbid();
            }

            await _printService.DeletePrint(existingPrint);

            _telemetry.TrackEvent("PrintDeleted");

            return Ok();
        }

        

        [AllowAnonymous]
        [HttpGet("{printId}/image/{imageId}")]
        [ResponseCache(Duration = 604800, Location = ResponseCacheLocation.Client, NoStore = false)]
        public async Task<IActionResult> GetImage(long printId, int imageId)
        {
            var existingPrint = await _context.Prints.FindAsync(printId);

            if (existingPrint == null)
            {
                return NotFound();
            }

            if (!await CanViewPrint(existingPrint))
            {
                return Forbid();
            }

            var imageFile = existingPrint.Images.Where(i => i.Id == imageId).Select(i => i.File).Single();

            try
            {

                var printImageDto = await _printImageService.DownloadPrintFile(imageFile);

                new FileExtensionContentTypeProvider().TryGetContentType(printImageDto.FileName, out var contentType);
                return File(printImageDto.File, contentType);

            } catch (DoesNotExistException)
            {
                return NotFound();
            }
        }

        [HttpPost("{printid}/image/{imageId}/set-as-default")]
        public async Task<ActionResult> SetImageAsDefault(long printid, int imageId)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var print = await _context.Prints.FindAsync(printid);

            if (print == null || !print.Images.Any(i => i.Id == imageId))
            {
                return NotFound();
            }

            // You can only change defaults for prints you created.
            if (userId != print.CreatedById)
            {
                return Forbid();
            }

            await _printService.SetDefaultImage(printid, imageId);
            
            return Ok();

            //return CreatedAtAction("GetPrint", new { id = newPrint.Id }, _mapper.Map<PrintDetailDTO>(newPrint));
        }

        [HttpPost("{id}/image")]
        public async Task<ActionResult> PostImage(long id, [FromForm] IFormFile image, [FromForm] bool isDefault = false)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var print = await _context.Prints.FindAsync(id);

            if (print == null)
            {
                return NotFound();
            }

            // You can only upload images for prints you own.
            if (userId != print.CreatedById)
            {
                return Forbid();
            }

            //foreach (IFormFile image in images)
            //{
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
                CreatedById = userId.Value,
                UpdatedById = userId.Value,
            };
            _context.Files.Add(file);

            var printImage = new PrintImage()
            {
                File = file,
                CreatedById = userId.Value,
                UpdatedById = userId.Value,
                Print = print,
                IsDefault = isDefault,
            };
            _context.PrintImages.Add(printImage);

            //}

            if (isDefault)
            {
                // Set other defaults to false;
                var otherEntities = await _context.PrintImages.Where(p => p.PrintId == id && p.IsDefault == true && p.FileId != fileId).ToListAsync();
                otherEntities.ForEach(p => p.IsDefault = false);
            }

            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("PrintPictureAdded");

            return Ok();

            //return CreatedAtAction("GetPrint", new { id = newPrint.Id }, _mapper.Map<PrintDetailDTO>(newPrint));
        }

        [HttpDelete("{printid}/image/{imageId}")]
        public async Task<ActionResult> RemoveImage(long printid, int imageId)
        {
            var userId = User.GetUserId();

            var print = await _context.Prints.FindAsync(printid);

            if (print == null || !print.Images.Any(i => i.Id == imageId))
            {
                return NotFound();
            }

            if (!userId.HasValue || userId != print.CreatedById)
            {
                return Forbid();
            }

            var selectedImage = await _context.PrintImages.FindAsync(imageId);
            _context.PrintImages.Remove(selectedImage);

            await _context.SaveChangesAsync();

            return Ok();
        }

        // POST: api/Prints
        [HttpPost("{printId}/comment")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "BindRequired used.")]
        public async Task<ActionResult<CommentDetailDto>> PostPrintComment(long printId, [FromBody, BindRequired] AddCommentDto newComment)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var print = await _context.Prints.FindAsync(printId);

            if (print == null)
            {
                return NotFound();
            }

            // Validation for adding new comments.
            if (!print.AllowComments)
            {
                return BadRequest("Comments are disabled for this print.");
            }

            // Only the original creator should be able to comment on private prints.
            if (print.ViewStatus == Print.PrintViewStatus.Private && userId != print.CreatedById)
            {
                return Forbid();
            }

            var comment = await _printService.AddPrintComment(print, newComment.Body, userId.Value);

            var mappedComment = await _commentService.GetCommentDetailById(comment.Id);

            return mappedComment;
        }

        /// <summary>
        /// Returns an array of all the IDs for public prints, for use with creating and updating sitemaps.
        /// </summary>
        /// <returns></returns>
        [AllowAnonymous]
        [HttpGet("public")]
        [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any, NoStore = false)]
        public async Task<ActionResult<IEnumerable<long>>> GetPublicPrintIds()
        {
            this._telemetry.TrackEvent("PublicPrintsQueried");
            return await this._context.Prints.Where(p => p.ViewStatus == PrintViewStatus.Public).Select(p => p.Id).ToListAsync();
        }

        /// <summary>
        /// Helper method to  check if the current user can view print
        /// </summary>
        /// <param name="print"></param>
        /// <returns></returns>
        private async Task<bool> CanViewPrint(Print print)
        {
            var authorizationResult = await _authorizationService
                            .AuthorizeAsync(User, print, "ViewPrint");

            return authorizationResult.Succeeded;

        }

        private bool PrintExists(long id)
        {
            return _context.Prints.Any(e => e.Id == id);
        }
    }
}
