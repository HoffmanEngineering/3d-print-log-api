using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Azure.Storage.Blobs;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PrintLogApi;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Comments;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.SortEnums;
using File = PrintLogApi.Models.File;

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

        private readonly string printImageContainerName = "printimages";
        private readonly BlobContainerClient printImageContainer;

        public PrintsController(PrintLogContext context, IMapper mapper, IConfiguration config, IAuthorizationService authorizationService, TelemetryClient telemetry)
        {
            _context = context;
            _mapper = mapper;
            _authorizationService = authorizationService;
            _telemetry = telemetry;

            BlobServiceClient blobServiceClient = new BlobServiceClient(config["AZURE_STORAGE_CONNECTION_STRING"]);
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

            long? currentUserId = null;
            try
            {
                currentUserId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);
            } catch (Exception ex)
            {
                currentUserId = null;
            }

            
            IQueryable<Print> printQuery;
            // if a userId is provided, filter by 
            if (userId.HasValue && userId != currentUserId)
            {
                // Get the user's public prints
                printQuery = _context.Prints
                .Where(p => p.CreatedById == userId)
                .Where(p => p.ViewStatus == Print.PrintViewStatus.Public);

            } else
            {
                // Throw a bad request if we aren't filtering by a user, and the current user isn't logged in.
                if (!currentUserId.HasValue)
                {
                    return BadRequest("User is not logged in, and summary is not filtered by a specific userId. Please log in and try again.");
                }

                printQuery = _context.Prints
                .Where(p => p.CreatedById == currentUserId || p.Printer.UserId == currentUserId);
            }
            

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                printQuery = printQuery.Where(p => p.Title.Contains(searchText) || p.Notes.Contains(searchText));
            }

            if (filterByStatus != null)
            {
                printQuery = printQuery.Where(p => p.Status == filterByStatus);
            }


            if (sortRequest != null)
            {
                if (sortRequest.SortColumn == PrintSummarySortColumn.Title)
                {
                    if (sortRequest.SortDirection == SortDirection.Asc)
                    {
                        printQuery = printQuery.OrderBy(p => p.Title).ThenByDescending(p => p.CreatedDate);
                    } else
                    {
                        printQuery = printQuery.OrderByDescending(p => p.Title).ThenByDescending(p => p.CreatedDate);
                    }
                } else
                {
                    if (sortRequest.SortDirection == SortDirection.Asc)
                    {
                        printQuery = printQuery.OrderBy(p => p.StartDate).ThenByDescending(p => p.CreatedDate);
                    }
                    else
                    {
                        printQuery = printQuery.OrderByDescending(p => p.StartDate).ThenByDescending(p => p.CreatedDate);
                    }
                }
            } else
            {
                printQuery = printQuery.OrderByDescending(p => p.StartDate).ThenByDescending(p => p.CreatedDate);
            }
            

            var prints = printQuery
                .ProjectTo<PrintSummaryDTO>(_mapper.ConfigurationProvider)
                .AsNoTracking();

            var response = await PagedList<PrintSummaryDTO>.CreateAsync(prints, pagingRequest.PageNumber, pagingRequest.PageSize);
            return response;
        }

        /// <summary>
        /// Get Print Statistics
        /// </summary>
        /// <returns></returns>
        [HttpGet("statistics")]
        public async Task<ActionResult<object>> GetPrintStatistics([FromQuery] DateTimeOffset fromDate, [FromQuery] DateTimeOffset toDate)
        {
            var userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            var baseQuery = _context.Prints
                .Where(p => p.CreatedById == userId || p.Printer.UserId == userId)
                .Where(p => p.StartDate >= fromDate && p.StartDate <= toDate);

            var numberOfPrints = await baseQuery.CountAsync();
            var groupByStatus = await baseQuery
                .GroupBy(p => p.Status)
                .Select(group => new { status = group.Key, count = group.Count() })
                .ToListAsync();

            var estimatedPrintTime = await baseQuery
                .Where(p => p.EstimatedPrintTimeInSeconds.HasValue)
                .Select(p => p.EstimatedPrintTimeInSeconds)
                .SumAsync();
            var totalPrintTime = await baseQuery
                .Where(p => p.PrintTimeInSeconds.HasValue)
                .Select(p => p.PrintTimeInSeconds)
                .SumAsync();

            var estimatedFilamentUsage = await baseQuery
                .Where(p => p.EstimatedFilamentUsageMg.HasValue)
                .Select(p => p.EstimatedFilamentUsageMg)
                .SumAsync();
            var totalFilamentUsage = await baseQuery
                .Where(p => p.FilamentUsageMg.HasValue)
                .Select(p => p.FilamentUsageMg)
                .SumAsync();

            var printTimeForPrinters = await baseQuery
                .Where(p => p.PrintTimeInSeconds.HasValue || p.EstimatedPrintTimeInSeconds.HasValue)
                .Select(p => new { printerId = p.PrinterId, printTime = p.PrintTimeInSeconds.HasValue ? p.PrintTimeInSeconds : p.EstimatedPrintTimeInSeconds })
                .GroupBy(p => p.printerId)
                .Select(group => new
                {
                    printerId = group.Key,
                    printTime = group.Sum(p => p.printTime)
                })
                .ToListAsync();

            return Ok(new { numberOfPrints, groupByStatus, estimatedPrintTime, totalPrintTime, estimatedFilamentUsage, totalFilamentUsage, printTimeForPrinters });
        }

        /// <summary>
        /// Get Print Statistics
        /// </summary>
        /// <returns></returns>
        [HttpGet("stats")]
        [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Client, NoStore = false)]
        public async Task<ActionResult<List<PrintStatistic>>> GetPrintStats([FromQuery] DateTimeOffset fromDate, [FromQuery] DateTimeOffset toDate)
        {
            var userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            var printStats = await _context.Prints
                .Where(p => p.CreatedById == userId || p.Printer.UserId == userId)
                .Where(p => p.StartDate >= fromDate && p.StartDate <= toDate)
                .OrderByDescending(p => p.StartDate)
                .ProjectTo<PrintStatistic>(_mapper.ConfigurationProvider)
                .AsNoTracking()
                .ToListAsync();

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

            return _mapper.Map<PrintDetailDTO>(print);
        }

        

        // PUT: api/Prints/5
        [HttpPut("{id}")]
        public async Task<ActionResult<PrintDetailDTO>> PutPrint(long id, PrintDetailDTO printDTO)
        {
            if (id != printDTO.Id)
            {
                return BadRequest();
            }

            Print existingPrint = await _context.Prints.FindAsync(id);

            if (existingPrint == null)
            {
                return NotFound();
            }


            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            if (userId != existingPrint.CreatedById || userId != existingPrint.Printer.UserId)
            {
                return Forbid();
            }

            existingPrint = _mapper.Map<PrintDetailDTO, Print>(printDTO, existingPrint);

            var printer = await _context.Printers.FindAsync(printDTO.PrinterId);
            existingPrint.Printer = printer;

            // Check if the user had access to that printer!
            if (userId != printer.UserId)
            {
                return BadRequest();
            }

            // Set UpdatedByIds

            existingPrint.UpdatedById = userId;


            _context.Entry(existingPrint).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!PrintExists(id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }

            _telemetry.TrackEvent("PrintEdit");

            return CreatedAtAction("GetPrint", new { id = existingPrint.Id }, _mapper.Map<PrintDetailDTO>(existingPrint));
        }

        // POST: api/Prints
        [HttpPost]
        public async Task<ActionResult<PrintDetailDTO>> PostPrint(AddPrintDTO print)
        {
            Print newPrint = _mapper.Map<Print>(print);

            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            newPrint.CreatedById = userId;
            newPrint.UpdatedById = userId;


            _context.Prints.Add(newPrint);
            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("PrintAdded");

            return CreatedAtAction("GetPrint", new { id = newPrint.Id }, _mapper.Map<PrintDetailDTO>(newPrint));
        }

        [AllowAnonymous]
        [HttpGet("{printId}/image/{imageId}")]
        [ResponseCache(Duration = 604800, Location = ResponseCacheLocation.Client, NoStore = false)]
        public async Task<IActionResult> GetImage(long printId,  int imageId)
        {
            Print existingPrint = await _context.Prints.FindAsync(printId);

            if (existingPrint == null)
            {
                return NotFound();
            }

            if (!await CanViewPrint(existingPrint))
            {
                return Forbid();
            }

            File imageFile = existingPrint.Images.Where(i => i.Id == imageId).Select(i => i.File).Single();

            var fileName = Path.GetFileName(imageFile.Path);
            BlobClient blobClient = printImageContainer.GetBlobClient(fileName);

            if ( await blobClient.ExistsAsync())
            {
                MemoryStream ms = new MemoryStream();
                var stream = await blobClient.DownloadToAsync(ms);
                ms.Position = 0;

                new FileExtensionContentTypeProvider().TryGetContentType(fileName, out string contentType);
                return File(ms, contentType);

            } else
            {
                return NotFound();
            }
        }

        [HttpPost("{printid}/image/{imageId}/set-as-default")]
        public async Task<ActionResult> SetImageAsDefault(long printid, int imageId)
        {
            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

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

            var selectedImage = await _context.PrintImages.FindAsync(imageId);
            selectedImage.IsDefault = true;


            // Set other defaults to false;
            var otherEntities = await _context.PrintImages.Where(p => p.PrintId == printid && p.IsDefault == true && p.PrintId != imageId).ToListAsync();
            otherEntities.ForEach(p => p.IsDefault = false);

            await _context.SaveChangesAsync();

            return Ok();

            //return CreatedAtAction("GetPrint", new { id = newPrint.Id }, _mapper.Map<PrintDetailDTO>(newPrint));
        }

        [HttpPost("{id}/image")]
        public async Task<ActionResult> PostImage(long id, [FromForm] IFormFile image, [FromForm] bool isDefault = false)
        {
            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

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
            Guid fileId = Guid.NewGuid();
            string fileName = fileId + Path.GetExtension(image.FileName);



            BlobClient blobClient = printImageContainer.GetBlobClient(fileName);

            using (Stream uploadFileStream = image.OpenReadStream())
            {
                await blobClient.UploadAsync(uploadFileStream);
            };

            var file = new Models.File()
            {
                Size = image.Length,
                Path = $"{this.printImageContainerName}/{fileName}",
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
                IsDefault = isDefault,
            };
            _context.PrintImages.Add(printImage);            

            //}

            if(isDefault)
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
            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            var print = await _context.Prints.FindAsync(printid);

            if (print == null || !print.Images.Any(i => i.Id == imageId))
            {
                return NotFound();
            }

            if (userId != print.CreatedById)
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
        public async Task<ActionResult> PostPrintComment(long printId, [FromBody, BindRequired] AddCommentDto newComment)
        {
            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            var print = await _context.Prints.FindAsync(printId);

            if (print == null)
            {
                return NotFound();
            }


            Comment comment = new Comment()
            {
                Body = newComment.Body,
                CreatedById = userId,
                UpdatedById = userId,
                UserId = userId,
                ParentId = null,
            };
            _context.Comments.Add(comment);

            PrintComment printComment = new PrintComment()
            {
                Print = print,
                Comment = comment,
                CreatedById = userId,
                UpdatedById = userId,
            };
            _context.PrintComments.Add(printComment);

            await _context.SaveChangesAsync();

            return Ok();

            //Print newPrint = _mapper.Map<Print>(print);

            //long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            //newPrint.CreatedById = userId;
            //newPrint.UpdatedById = userId;


            //_context.Prints.Add(newPrint);
            //await _context.SaveChangesAsync();

            //_telemetry.TrackEvent("PrintAdded");

            //return CreatedAtAction("GetPrint", new { id = newPrint.Id }, _mapper.Map<PrintDetailDTO>(newPrint));
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
