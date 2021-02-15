using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using CsvHelper;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.SortEnums;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Services
{
    public class PrintService : IPrintService
    {

        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;
        private readonly IFilamentService _filamentService;

        public PrintService(PrintLogContext context, IMapper mapper, TelemetryClient telemetry, IFilamentService filamentService)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
            _filamentService = filamentService;
        }

        /// <summary>
        /// Returns a paged list of print summaries based on the search parameters.
        /// </summary>
        /// <param name="pagingRequest"></param>
        /// <param name="searchText"></param>
        /// <param name="sortRequest"></param>
        /// <param name="filterByStatus"></param>
        /// <param name="userId"></param>
        /// <param name="currentUserId"></param>
        /// <returns></returns>
        public async Task<PagedList<PrintSummaryDTO>> SearchPrintSummary(
            PagedRequest pagingRequest,
            string searchText,
            SortRequest<PrintSummarySortColumn> sortRequest,
            PrintStatus? filterByStatus,
            long? userId,
            long? currentUserId)
        {
            if (pagingRequest == null)
            {
                throw new ArgumentNullException(nameof(pagingRequest));
            }

            if (sortRequest == null)
            {
                throw new ArgumentNullException(nameof(sortRequest));
            }

            IQueryable<Print> printQuery;
            // if a userId is provided, filter by 
            if (userId.HasValue && userId != currentUserId)
            {
                // Get the user's public prints
                printQuery = _context.Prints
                .Where(p => p.CreatedById == userId)
                .Where(p => p.ViewStatus == Print.PrintViewStatus.Public);

            }
            else
            {

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
                    }
                    else
                    {
                        printQuery = printQuery.OrderByDescending(p => p.Title).ThenByDescending(p => p.CreatedDate);
                    }
                }
                else
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
            }
            else
            {
                printQuery = printQuery.OrderByDescending(p => p.StartDate).ThenByDescending(p => p.CreatedDate);
            }


            var prints = printQuery
                .ProjectTo<PrintSummaryDTO>(_mapper.ConfigurationProvider)
                .AsNoTracking();

            PagedList<PrintSummaryDTO> response = await PagedList<PrintSummaryDTO>.CreateAsync(prints, pagingRequest.PageNumber, pagingRequest.PageSize);
            return response;
        }

        public async Task<List<long>> GetPublicPrintIds()
        {
            return await this._context.Prints.Where(p => p.ViewStatus == PrintViewStatus.Public).Select(p => p.Id).ToListAsync();
        }

        public async Task<List<PrintStatistic>> GetPrintStatisticsForUser(long userId, DateTimeOffset fromDate, DateTimeOffset toDate)
        {
            return await _context.Prints
                            .Where(p => p.CreatedById == userId || p.Printer.UserId == userId)
                            .Where(p => p.StartDate >= fromDate && p.StartDate <= toDate)
                            .OrderByDescending(p => p.StartDate)
                            .ProjectTo<PrintStatistic>(_mapper.ConfigurationProvider)
                            .AsNoTracking()
                            .ToListAsync();
        }

        public async Task<Stream> GeneratePrintReportAsCsvForUser(long userId)
        {
            var prints = _context.Prints
                            .Where(p => p.CreatedById == userId || p.Printer.UserId == userId)
                            .OrderByDescending(p => p.StartDate).ThenByDescending(p => p.CreatedDate)
                            .ProjectTo<PrintDetailReport>(_mapper.ConfigurationProvider)
                            .AsNoTracking();


            List<PrintDetailReport> reportCSVModels = await prints.ToListAsync();
            var printCount = reportCSVModels.Count;

            var stream = new MemoryStream();

            using (var operation = _telemetry.StartOperation<DependencyTelemetry>("ConvertPrintReportToCsv"))
            using (var writeFile = new StreamWriter(stream, leaveOpen: true))
            using (var csv = new CsvWriter(writeFile, CultureInfo.InvariantCulture))
            {
                csv.Configuration.RegisterClassMap<PrintDetailReportMap>();
                csv.WriteRecords(reportCSVModels);

            }
            stream.Position = 0; //reset stream

            var lengthInBytes = stream.Length;
            var metrics = new Dictionary<string, double> { { "PrintCount", printCount }, { "ReportLengthInBytes", lengthInBytes } };
            _telemetry.TrackEvent("PrintReportExport", metrics: metrics);

            return stream;
        }

        public async Task<Print> GetPrintById(long id)
        {
            var print = await this._context.Prints
                .Include(p => p.Printer)
                .Include(p => p.Images)
                    .ThenInclude(p => p.File)
                .Include(p => p.Comments)
                    .ThenInclude(p => p.Comment)
                .Include(p => p.FilamentUsage)
                    .ThenInclude(pf => pf.Filament)
                .Where(p => p.Id == id)
                .FirstOrDefaultAsync();

            print.Comments = print.Comments.OrderBy(c => c.CreatedDate).ToList();

            return print;
        }

        /// <summary>
        /// Add Print
        /// </summary>
        /// <param name="print">The Print to add</param>
        /// <param name="userId">The user adding the print</param>
        /// <returns></returns>
        public async Task<Print> AddPrint(AddPrintDTO print, long userId)
        {
            var newPrint = _mapper.Map<Print>(print);

            var printer = await _context.Printers.FindAsync(print.PrinterId);
            newPrint.Printer = printer;

            // Check if the user had access to that printer!
            if (userId != printer.UserId)
            {
                //return BadRequest();
                throw new UserCannotAccessPrinterException();
            }

            foreach (var filament in newPrint.FilamentUsage)
            {
                // Set the empty guid to null
                if (filament.FilamentId.HasValue && filament.FilamentId == default(Guid))
                {
                    filament.FilamentId = null;
                }

                if (filament.FilamentId.HasValue)
                {
                    var canAccessFilament = await this._filamentService.CanUserAccessFilament(userId, filament.FilamentId.Value);
                    if (!canAccessFilament)
                    {
                        throw new UserCannotAccessFilamentException();
                    }
                }
                
            }

            await UpdateFilamentUsageWeights(newPrint);

            newPrint.CreatedById = userId;
            newPrint.UpdatedById = userId;


            _context.Prints.Add(newPrint);
            await _context.SaveChangesAsync();
            return await GetPrintById(newPrint.Id); ;
        }

        public async Task<Print> UpdatePrint(long id, PutPrintDetailDto dto, long userId)
        {
            var existingPrint = await GetPrintById(id);
            
            if (existingPrint == null)
            {
                throw new ArgumentNullException(nameof(id));
            }

            var updatedPrint = _mapper.Map<PutPrintDetailDto, Print>(dto, existingPrint);

            var printer = await _context.Printers.FindAsync(dto.PrinterId);
            updatedPrint.Printer = printer;

            // Check if the user had access to that printer!
            if (userId != printer.UserId)
            {
                //return BadRequest();
                throw new UserCannotAccessPrinterException();
            }

            foreach (var filament in updatedPrint.FilamentUsage)
            {
                // Set the empty guid to null
                if (filament.FilamentId.HasValue && filament.FilamentId == default(Guid))
                {
                    filament.FilamentId = null;
                }

                if (filament.FilamentId.HasValue)
                {
                    var canAccessFilament = await this._filamentService.CanUserAccessFilament(userId, filament.FilamentId.Value);
                    if (!canAccessFilament)
                    {
                        throw new UserCannotAccessFilamentException();
                    }
                }
            }

            await UpdateFilamentUsageWeights(updatedPrint);

            updatedPrint.UpdatedById = userId;


            _context.Entry(updatedPrint).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!PrintExists(id))
                {
                    throw new DoesNotExistException();
                }
                else
                {
                    throw;
                }
            }

            _telemetry.TrackEvent("PrintEdit");

            return await GetPrintById(updatedPrint.Id);
        }

        /// <summary>
        /// When we save the filament usage, we need to ensure that the filament weights and lengths are correctly filled out.
        /// </summary>


        private async Task UpdateFilamentUsageWeights(Print print)
        {
            foreach(var pf in print.FilamentUsage)
            {
                if (!pf.FilamentId.HasValue || pf.FilamentId == default(Guid))
                {
                    // We can't do anything for filament lengths not tied to a filament
                    continue;
                }

                var filament = await _filamentService.GetFilamentById(pf.FilamentId.Value);

                if (filament is null || !filament.DiameterMm.HasValue || !(filament.DiameterMm >= 0) || !(filament.MaterialDensityGramPerCubicCm >= 0) )
                {
                    // Skip any filament that doesn't have the required properties to compute.
                    continue;
                }

                if (pf.IsActualLengthSource)
                {

                    if (pf.LengthInM.HasValue)
                    {
                        pf.AmountMg = GetAmountMg(pf.LengthInM.Value, filament.DiameterMm.Value, filament.MaterialDensityGramPerCubicCm);
                    }
                } else
                {

                    if (pf.AmountMg.HasValue)
                    {
                        pf.LengthInM = GetLengthInMeters(pf.AmountMg.Value, filament.DiameterMm.Value, filament.MaterialDensityGramPerCubicCm);
                    }
                }

                if (pf.IsEstimatedLengthSource)
                {
                    // Update the weight
                    if (pf.EstimatedLengthInM.HasValue)
                    {
                        pf.EstimatedAmountMg = GetAmountMg(pf.EstimatedLengthInM.Value, filament.DiameterMm.Value, filament.MaterialDensityGramPerCubicCm);
                    }

                }
                else
                {
                    // Update the length
                    if (pf.EstimatedAmountMg.HasValue)
                    {
                        pf.EstimatedLengthInM = GetLengthInMeters(pf.EstimatedAmountMg.Value, filament.DiameterMm.Value, filament.MaterialDensityGramPerCubicCm);
                    }
                }
            }
        }

        private static int GetAmountMg(double lengthInMeters, double filamentDiameterInMM, double materialDensityGramPerCubicCm)
        {
            return (int)Math.Round(250.0 * Math.PI * materialDensityGramPerCubicCm * filamentDiameterInMM * filamentDiameterInMM * lengthInMeters);
        }

        /// <summary>
        /// Converts between an amount in milligrams to the expected length of that filament in meters.
        /// </summary>
        /// <returns></returns>
        private static double GetLengthInMeters(int AmountMg, double filamentDiameterInMM, double materialDensityGramPerCubicCm)
        {
            return ((double)AmountMg) / ((250.0 * Math.PI * materialDensityGramPerCubicCm * filamentDiameterInMM * filamentDiameterInMM));
        }

        public async Task<Print> UpdatePrintStatus(long id, PrintStatus newStatus, long userId)
        {
            var existingPrint = await GetPrintById(id);

            if (existingPrint == null)
            {
                throw new ArgumentNullException(nameof(id));
            }

            // Set the new status
            existingPrint.Status = newStatus;
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
                    throw new DoesNotExistException();
                }
                else
                {
                    throw;
                }
            }

            _telemetry.TrackEvent("PrintStatusEdit");

            return existingPrint;
        }

        public async Task SetDefaultImage(long printId, long newDefaultImageId)
        {
            var print = await GetPrintById(printId);

            if (print == null)
            {
                throw new ArgumentNullException(nameof(printId));
            }

            var selectedImage = await _context.PrintImages.FindAsync(newDefaultImageId);
            selectedImage.IsDefault = true;

            // Set other defaults to false;
            var otherEntities = await _context.PrintImages.Where(p => p.PrintId == printId && p.IsDefault == true && p.PrintId != newDefaultImageId).ToListAsync();
            otherEntities.ForEach(p => p.IsDefault = false);

            await _context.SaveChangesAsync();
        }

        public async Task DeletePrint(Print print)
        {
            if (print == null)
            {
                throw new ArgumentNullException(nameof(print));
            }

            foreach (var comment in print.Comments.ToArray())
            {
                _context.Comments.Remove(comment.Comment);
            }
            _context.PrintComments.RemoveRange(print.Comments.ToArray());

            foreach (var image in print.Images.ToArray())
            {
                _context.Files.Remove(image.File);
            }
            _context.PrintImages.RemoveRange(print.Images.ToArray());

            _context.Prints.Remove(print);

            await _context.SaveChangesAsync();
        }


        /// <summary>
        /// Adds a comment to a print
        /// </summary>
        /// <param name="print"></param>
        /// <param name="commentBody"></param>
        /// <param name="userId"></param>
        /// <param name=""></param>
        /// <returns></returns>
        public async Task<Comment> AddPrintComment(Print print, string commentBody, long userId)
        {
            var comment = new Comment()
            {
                Body = commentBody.Trim(),
                CreatedById = userId,
                UpdatedById = userId,
            };
            _context.Comments.Add(comment);

            var printComment = new PrintComment()
            {
                Print = print,
                Comment = comment,
                CreatedById = userId,
                UpdatedById = userId,
            };
            _context.PrintComments.Add(printComment);

            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("CommentAdded");
            return comment;
        }
        private bool PrintExists(long id)
        {
            return _context.Prints.Any(e => e.Id == id);
        }
    }
}
