using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using CsvHelper;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using PrintLogApi.Models.SortEnums;
using static PrintLogApi.Models.Print;
using static PrintLogApi.Services.MeasurementUtilities;

namespace PrintLogApi.Services
{
    public class PrintService : IPrintService
    {

        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;
        private readonly IFilamentService _filamentService;
        private readonly IPrinterService _printerService;
        private readonly INotificationService _notificationService;

        public PrintService(PrintLogContext context,
                            IMapper mapper,
                            TelemetryClient telemetry,
                            IFilamentService filamentService,
                            IPrinterService printerService,
                            INotificationService notificationService)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
            _filamentService = filamentService;
            _printerService = printerService;
            _notificationService = notificationService;
        }

        /// <summary>
        /// Returns a paged list of print summaries based on the search parameters.
        /// </summary>
        /// <param name="pagingRequest"></param>
        /// <param name="searchText"></param>
        /// <param name="sortRequest"></param>
        /// <param name="filterByPrinterIds"></param>
        /// <param name="filterByFilamentIds"></param>
        /// <param name="filterByStatus"></param>
        /// <param name="userId"></param>
        /// <param name="currentUserId"></param>
        /// <returns></returns>
        public async Task<PagedList<PrintSummaryDTO>> SearchPrintSummary(
            PagedRequest pagingRequest,
            string searchText,
            SortRequest<PrintSummarySortColumn> sortRequest,
            IEnumerable<long> filterByPrinterIds,
            IEnumerable<Guid> filterByFilamentIds,
            PrintStatus? filterByStatus,
            long? userId,
            long? currentUserId,
            Guid? filterByProjectId = null)
        {
            if (pagingRequest == null)
            {
                throw new ArgumentNullException(nameof(pagingRequest));
            }

            if (sortRequest == null)
            {
                throw new ArgumentNullException(nameof(sortRequest));
            }

            var printerIdList = filterByPrinterIds?.ToList();
            var filamentIdList = filterByFilamentIds?.ToList();

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
                    .Where(p => p.CreatedById == currentUserId);
            }

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                // Split on any spaces and search separately, preserving quotes.
                var criterias = searchText.Split('"')
                     .Select((element, index) => index % 2 == 0  // If even index
                                           ? element.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)  // Split the item
                                           : new string[] { element })  // Keep the entire item
                     .SelectMany(element => element).ToList();
                foreach (var text in criterias)
                {
                    printQuery = printQuery.Where(p => p.Title.Contains(text) || p.Notes.Contains(text));
                }
            }

            if (filterByStatus != null)
            {
                printQuery = printQuery.Where(p => p.Status == filterByStatus);
            }

            // Filter by an of the selected printer ids.
            if (printerIdList != null && printerIdList.Any())
            {
                printQuery = printQuery.Where(p => printerIdList.Contains(p.PrinterId));
            }

            // Filter by any of the selected filament ids.
            if (filamentIdList != null && filamentIdList.Any())
            {
                printQuery = printQuery.Where(p => p.FilamentUsage.Any(pf => pf.FilamentId.HasValue && filamentIdList.Contains((Guid)pf.FilamentId)));
            }

            if (filterByProjectId.HasValue)
            {
                printQuery = printQuery.Where(p => p.ProjectId == filterByProjectId.Value);
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
                else if (sortRequest.SortColumn == PrintSummarySortColumn.StartDate)
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
                else if (sortRequest.SortColumn == PrintSummarySortColumn.FilamentUsage)
                {
                    // NOTE: This is still problematic - consider computing and storing this value
                    if (sortRequest.SortDirection == SortDirection.Asc)
                    {
                        printQuery = printQuery.OrderBy(src => src.FilamentUsage.Sum(p => p.AmountMg.HasValue &&
                                                                                                    p.AmountMg > 0 ?
                                                                                                    p.AmountMg :
                                                                                                    p.EstimatedAmountMg.HasValue &&
                                                                                                    p.EstimatedAmountMg > 0 ?
                                                                                                    p.EstimatedAmountMg : 0)).ThenByDescending(p => p.CreatedDate);
                    }
                    else
                    {
                        printQuery = printQuery.OrderByDescending(src => src.FilamentUsage.Sum(p => p.AmountMg.HasValue &&
                                                                                                    p.AmountMg > 0 ?
                                                                                                    p.AmountMg :
                                                                                                    p.EstimatedAmountMg.HasValue &&
                                                                                                    p.EstimatedAmountMg > 0 ?
                                                                                                    p.EstimatedAmountMg : 0)).ThenByDescending(p => p.CreatedDate);
                    }
                }
            }
            else
            {
                printQuery = printQuery.OrderByDescending(p => p.StartDate).ThenByDescending(p => p.CreatedDate);
            }

            // **KEY CHANGE: Select only the Print IDs first**
            var printIds = await printQuery
                .Select(p => p.Id)
                .Skip((pagingRequest.PageNumber - 1) * pagingRequest.PageSize)
                .Take(pagingRequest.PageSize)
                .ToListAsync();

            var totalCount = await printQuery.CountAsync();

            // **Now load the full data for just these IDs with explicit includes**
            var prints = await _context.Prints
                .Where(p => printIds.Contains(p.Id))
                .Include(p => p.Printer)
                    .ThenInclude(pr => pr.Category)
                        .ThenInclude(c => c.MaterialCategory)
                .Include(p => p.FilamentUsage)
                    .ThenInclude(pf => pf.Filament)
                        .ThenInclude(f => f.MaterialCategory)
                .Include(p => p.Images)
                .Include(p => p.Project)
                .AsNoTracking()
                .AsSplitQuery()  // Now we CAN use split query!
                .ToListAsync();

            // **Project to DTO in-memory (more efficient than complex DB projection)**
            var dtos = prints
                .Select(p => _mapper.Map<PrintSummaryDTO>(p))
                .ToList();

            // **Restore original sort order**
            var dtoById = dtos.ToDictionary(d => d.Id);
            var orderedDtos = printIds
                .Select(id => dtoById[id])
                .ToList();

            return new PagedList<PrintSummaryDTO>(
                orderedDtos,
                totalCount,
                pagingRequest.PageNumber,
                pagingRequest.PageSize);
        }

        public async Task<List<long>> GetPublicPrintIds()
        {
            return await this._context.Prints.Where(p => p.ViewStatus == PrintViewStatus.Public).Select(p => p.Id).ToListAsync();
        }

        public async Task<List<PrintStatistic>> GetPrintStatisticsForUser(long userId, DateTimeOffset fromDate, DateTimeOffset toDate)
        {
            return await _context.Prints
                            .Where(p => p.CreatedById == userId)
                            .Where(p => p.StartDate >= fromDate && p.StartDate <= toDate)
                            .OrderByDescending(p => p.StartDate)
                            .ThenByDescending(p => p.CreatedDate)
                            .ThenByDescending(p => p.Id)
                            .ProjectTo<PrintStatistic>(_mapper.ConfigurationProvider)
                            .AsNoTracking()
                            .ToListAsync();
        }

        public async Task<Stream> GeneratePrintReportAsCsvForUser(long userId)
        {
            var prints = _context.Prints
                            .Where(p => p.CreatedById == userId)
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
                csv.Context.RegisterClassMap<PrintDetailReportMap>();
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
                .AsSplitQuery()
                .FirstOrDefaultAsync();

            if (print is not null)
            {
                print.Comments = print.Comments.OrderBy(c => c.CreatedDate).ThenBy(c => c.Id).ToList();
            }

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

            var printer = await _context.Printers
                .Include(p => p.LoadedFilaments)
                .Where(p => p.Id == print.PrinterId)
                .AsSplitQuery()
                .FirstOrDefaultAsync();
            if (printer == null)
            {
                throw new UserCannotAccessPrinterException();
            }
            newPrint.Printer = printer;

            // Check if the user had access to that printer!
            if (userId != printer.UserId)
            {
                //return BadRequest();
                throw new UserCannotAccessPrinterException();
            }

            foreach (var filament in newPrint.FilamentUsage)
            {
                if (filament.FilamentId.HasValue && filament.FilamentId == default(Guid))
                    filament.FilamentId = null;
            }

            var filamentIdsToCheck = newPrint.FilamentUsage
                .Where(f => f.FilamentId.HasValue)
                .Select(f => f.FilamentId.Value);

            if (!await _filamentService.CanUserAccessAllFilaments(userId, filamentIdsToCheck))
            {
                throw new UserCannotAccessFilamentException();
            }

            var newLoadedFilamentIds = newPrint.FilamentUsage
                .Where(filament => filament.FilamentId.HasValue && filament.FilamentId != default)
                .Select(filament => filament.FilamentId.Value);

            // PrinterService setLoadedFilament
            await _printerService.setLoadedFilament(newPrint.Printer.Id, newLoadedFilamentIds);


            await UpdateFilamentUsageWeights(newPrint);

            newPrint.CreatedById = userId;
            newPrint.UpdatedById = userId;

            // Resolve project assignment
            if (print.ProjectId.HasValue)
            {
                var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == print.ProjectId.Value && p.CreatedById == userId);
                if (project == null) throw new DoesNotExistException();
                newPrint.ProjectId = project.Id;
            }
            else if (!string.IsNullOrWhiteSpace(print.NewProjectName))
            {
                var newProject = new Project
                {
                    Id = Guid.NewGuid(),
                    Name = print.NewProjectName.Trim(),
                    Status = Project.ProjectStatus.InProgress,
                    ViewStatus = Project.ProjectViewStatus.Private,
                    CreatedById = userId,
                    UpdatedById = userId
                };
                _context.Projects.Add(newProject);
                newPrint.ProjectId = newProject.Id;
            }

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
                if (filament.FilamentId.HasValue && filament.FilamentId == default(Guid))
                    filament.FilamentId = null;
            }

            var updatedFilamentIdsToCheck = updatedPrint.FilamentUsage
                .Where(f => f.FilamentId.HasValue)
                .Select(f => f.FilamentId.Value);

            if (!await _filamentService.CanUserAccessAllFilaments(userId, updatedFilamentIdsToCheck))
            {
                throw new UserCannotAccessFilamentException();
            }

            await UpdateFilamentUsageWeights(updatedPrint);

            updatedPrint.UpdatedById = userId;

            // Resolve project assignment
            if (dto.ProjectId.HasValue)
            {
                var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == dto.ProjectId.Value && p.CreatedById == userId);
                if (project == null) throw new DoesNotExistException();
                updatedPrint.ProjectId = project.Id;
            }
            else if (!string.IsNullOrWhiteSpace(dto.NewProjectName))
            {
                var newProject = new Project
                {
                    Id = Guid.NewGuid(),
                    Name = dto.NewProjectName.Trim(),
                    Status = Project.ProjectStatus.InProgress,
                    ViewStatus = Project.ProjectViewStatus.Private,
                    CreatedById = userId,
                    UpdatedById = userId
                };
                _context.Projects.Add(newProject);
                updatedPrint.ProjectId = newProject.Id;
            }
            else
            {
                // Explicit null clears the project assignment
                updatedPrint.ProjectId = dto.ProjectId; // null
            }

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
        /// When we save the filament usage, we need to ensure that the filament weights, lengths and volume are correctly filled out.
        /// </summary>
        public async Task UpdateFilamentUsageWeights(Print print)
        {
            var filamentIds = print.FilamentUsage
                .Where(pf => pf.FilamentId.HasValue && pf.FilamentId != default(Guid))
                .Select(pf => pf.FilamentId.Value)
                .Distinct()
                .ToList();

            if (filamentIds.Count == 0) return;

            var filamentMap = await _context.Filaments
                .Where(f => filamentIds.Contains(f.Id))
                .Include(f => f.MaterialCategory)
                .AsNoTracking()
                .ToDictionaryAsync(f => f.Id);

            foreach (var pf in print.FilamentUsage)
            {
                if (!pf.FilamentId.HasValue || pf.FilamentId == default(Guid))
                {
                    continue;
                }

                if (!filamentMap.TryGetValue(pf.FilamentId.Value, out var filament))
                {
                    continue;
                }

                bool hasDiameter = filament.MaterialCategory?.HasDiameter == true;

                if (filament.MaterialDensityGramPerCubicCm <= 0
                    || (hasDiameter && (!filament.DiameterMm.HasValue || filament.DiameterMm <= 0)))
                {
                    continue;
                }

                if (pf.Source == PrintFilament.SourceMeasurement.Length)
                {
                    if (pf.LengthInM.HasValue)
                    {
                        pf.AmountMg = (int)GetAmountMgFromLength(pf.LengthInM.Value, filament.DiameterMm.Value, filament.MaterialDensityGramPerCubicCm);
                        pf.VolumeMl = GetVolumeInMlFromLengthM(pf.LengthInM.Value, filament.DiameterMm.Value);
                    }
                }
                else if (pf.Source == PrintFilament.SourceMeasurement.Volume)
                {
                    if (pf.VolumeMl.HasValue)
                    {
                        pf.AmountMg = (int)GetAmountMgFromVolume(pf.VolumeMl.Value, filament.MaterialDensityGramPerCubicCm);

                        if (hasDiameter)
                        {
                            pf.LengthInM = GetLengthInMetersFromVolume(pf.VolumeMl.Value, filament.DiameterMm.Value);
                        }
                    }
                }
                else
                {
                    if (pf.AmountMg.HasValue)
                    {
                        pf.VolumeMl = GetVolumeInMlFromAmount(pf.AmountMg.Value, filament.MaterialDensityGramPerCubicCm);

                        if (hasDiameter)
                        {
                            pf.LengthInM = GetLengthInMetersFromAmount(pf.AmountMg.Value, filament.DiameterMm.Value, filament.MaterialDensityGramPerCubicCm);
                        }
                    }
                }

                if (pf.EstimatedSource == PrintFilament.SourceMeasurement.Length)
                {
                    if (pf.EstimatedLengthInM.HasValue)
                    {
                        pf.EstimatedAmountMg = (int)GetAmountMgFromLength(pf.EstimatedLengthInM.Value, filament.DiameterMm.Value, filament.MaterialDensityGramPerCubicCm);
                        pf.EstimatedVolumeMl = GetVolumeInMlFromLengthM(pf.EstimatedLengthInM.Value, filament.DiameterMm.Value);
                    }
                }
                else if (pf.EstimatedSource == PrintFilament.SourceMeasurement.Volume)
                {
                    if (pf.EstimatedVolumeMl.HasValue)
                    {
                        pf.EstimatedAmountMg = (int)GetAmountMgFromVolume(pf.EstimatedVolumeMl.Value, filament.MaterialDensityGramPerCubicCm);

                        if (hasDiameter)
                        {
                            pf.EstimatedLengthInM = GetLengthInMetersFromVolume(pf.EstimatedVolumeMl.Value, filament.DiameterMm.Value);
                        }
                    }
                }
                else
                {
                    if (pf.EstimatedAmountMg.HasValue)
                    {
                        pf.EstimatedVolumeMl = GetVolumeInMlFromAmount(pf.EstimatedAmountMg.Value, filament.MaterialDensityGramPerCubicCm);

                        if (hasDiameter)
                        {
                            pf.EstimatedLengthInM = GetLengthInMetersFromAmount(pf.EstimatedAmountMg.Value, filament.DiameterMm.Value, filament.MaterialDensityGramPerCubicCm);
                        }
                    }
                }
            }
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

        public async Task<int> GetMaxImagesPerPrint(long userId)
        {
            var subscription = await _context.Subscriptions
                .Where(s => s.UserId == userId)
                .AsNoTracking()
                .SingleOrDefaultAsync();

            return subscription?.Status == SubscriptionStatus.Active
                ? SubscriptionLimits.ProMaxImagesPerPrint
                : SubscriptionLimits.FreeMaxImagesPerPrint;
        }

        public async Task SetDefaultImage(long printId, int newDefaultImageId)
        {
            var print = await GetPrintById(printId);

            if (print == null)
            {
                throw new ArgumentNullException(nameof(printId));
            }

            var selectedImage = await _context.PrintImages.FindAsync(newDefaultImageId);
            selectedImage.IsDefault = true;

            // Set other defaults to false;
            var otherEntities = await _context.PrintImages.Where(p => p.PrintId == printId && p.IsDefault == true && p.Id != newDefaultImageId).ToListAsync();
            otherEntities.ForEach(p => p.IsDefault = false);

            await _context.SaveChangesAsync();
        }

        public async Task DeletePrint(Print print)
        {
            if (print == null)
            {
                throw new ArgumentNullException(nameof(print));
            }

            // Remove Print Comments.
            foreach (var comment in print.Comments.ToArray())
            {
                _context.Comments.Remove(comment.Comment);
            }
            _context.PrintComments.RemoveRange(print.Comments.ToArray());

            // Remove Print Images.
            foreach (var image in print.Images.ToArray())
            {
                _context.Files.Remove(image.File);
            }
            _context.PrintImages.RemoveRange(print.Images.ToArray());

            // Remove Print Attachments.
            var attachments = await _context.PrintAttachments
                .Include(a => a.File)
                .Where(a => a.PrintId == print.Id)
                .ToListAsync();
            foreach (var attachment in attachments)
            {
                _context.Files.Remove(attachment.File);
            }
            _context.PrintAttachments.RemoveRange(attachments);

            // Remove PrintFilament for this print.
            _context.PrintFilament.RemoveRange(print.FilamentUsage.ToArray());

            // Remove Notifications referencing this print.
            var notifications = await _context.Notifications
                .Where(n => n.PrintId == print.Id)
                .ToListAsync();
            _context.Notifications.RemoveRange(notifications);

            _context.Prints.Remove(print);

            await _context.SaveChangesAsync();
        }


        /// <summary>
        /// Adds a comment to a print
        /// </summary>
        /// <param name="print"></param>
        /// <param name="commentBody"></param>
        /// <param name="userId"></param>
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

            // Get commenter info for notifications
            var commenter = await _context.Users.FindAsync(userId);
            var commenterDisplayName = commenter?.DisplayName ?? "Someone";

            // Build recipient list: print owner (if not the commenter) + previous unique commenters
            var previousCommenterIds = await _context.PrintComments
                .Where(pc => pc.PrintId == print.Id && pc.CommentId != comment.Id)
                .Select(pc => pc.Comment.CreatedById)
                .Distinct()
                .Where(id => id != userId && id != print.CreatedById)
                .ToListAsync();

            var recipients = new List<(long RecipientUserId, bool IsRecipientPrintOwner)>();
            if (print.CreatedById != userId)
                recipients.Add((print.CreatedById, true));
            recipients.AddRange(previousCommenterIds.Select(id => (id, false)));

            await _notificationService.CreateCommentNotifications(
                recipients,
                print.Id,
                print.Title,
                comment.Id,
                userId,
                commenterDisplayName);

            return comment;
        }
        private bool PrintExists(long id)
        {
            return _context.Prints.Any(e => e.Id == id);
        }

        private sealed class FeedSortItem
        {
            public string Id { get; init; }
            public string Type { get; init; }
            public DateTimeOffset SortDate { get; init; }
            public string SortTitle { get; init; }
            public long TotalFilamentWeightMg { get; init; }
        }

        public async Task<List<PrintFeedSummaryDto>> GetPrintFeedSummary(long? currentUserId, int numberOfRecords, DateTimeOffset fromDateTime)
        {
            // TODO: Use the currentUserId to filter the feed based on friends, likes, etcetc

            var prints = await _context.Prints
                .Where(p => p.CreatedDate < fromDateTime)
                .Where(p => p.ViewStatus == PrintViewStatus.Public) // Only show public prints
                .OrderByDescending(p => p.CreatedDate)
                .Take(numberOfRecords)
                .ProjectTo<PrintFeedSummaryDto>(_mapper.ConfigurationProvider)
                .AsNoTracking()
                .ToListAsync();

            return prints;
        }

        public async Task<PagedList<GroupedFeedItemDto>> GetGroupedFeedAsync(
            int pageNumber,
            int pageSize,
            long userId,
            string searchText = null,
            IEnumerable<long> filterByPrinterIds = null,
            IEnumerable<Guid> filterByFilamentIds = null,
            Print.PrintStatus? filterByStatus = null,
            SortRequest<PrintSummarySortColumn> sortRequest = null)
        {
            var printerIdList = filterByPrinterIds?.ToList();
            var filamentIdList = filterByFilamentIds?.ToList();

            bool hasFilters = !string.IsNullOrWhiteSpace(searchText)
                || filterByStatus.HasValue
                || (printerIdList != null && printerIdList.Any())
                || (filamentIdList != null && filamentIdList.Any());

            // ── Phase 1: Build the filtered print query ───────────────────────────────
            IQueryable<Print> filteredPrintQuery = _context.Prints
                .Where(p => p.CreatedById == userId);

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                var criterias = searchText.Split('"')
                    .Select((element, index) => index % 2 == 0
                        ? element.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        : new string[] { element })
                    .SelectMany(element => element).ToList();
                foreach (var text in criterias)
                    filteredPrintQuery = filteredPrintQuery.Where(p => p.Title.Contains(text) || p.Notes.Contains(text) || p.Project.Name.Contains(text));
            }

            if (filterByStatus.HasValue)
                filteredPrintQuery = filteredPrintQuery.Where(p => p.Status == filterByStatus.Value);

            if (printerIdList != null && printerIdList.Any())
                filteredPrintQuery = filteredPrintQuery.Where(p => printerIdList.Contains(p.PrinterId));

            if (filamentIdList != null && filamentIdList.Any())
            {
                filteredPrintQuery = filteredPrintQuery.Where(p =>
                    p.FilamentUsage.Any(pf => pf.FilamentId.HasValue && filamentIdList.Contains((Guid)pf.FilamentId)));
            }

            // ── Phase 2: Determine filtered print counts per project (only needed when filters are active) ──
            Dictionary<Guid, int> filteredGroupLookup;
            if (hasFilters)
            {
                var groups = await filteredPrintQuery
                    .Where(p => p.ProjectId != null)
                    .GroupBy(p => p.ProjectId)
                    .Select(g => new { ProjectId = g.Key, FilteredPrintCount = g.Count() })
                    .ToListAsync();
                filteredGroupLookup = groups
                    .Where(g => g.ProjectId.HasValue)
                    .ToDictionary(g => g.ProjectId.Value, g => g.FilteredPrintCount);
            }
            else
            {
                filteredGroupLookup = new Dictionary<Guid, int>();
            }

            // ── Phase 3: Lightweight sort-key queries (no navigation loads) ───────────
            // Projects: two queries joined in memory to avoid a correlated subquery per row.
            // Intentionally sums ALL project prints (not just filtered) so sort order reflects overall project weight.
            var projectList = await _context.Projects
                .Where(p => p.CreatedById == userId)
                .Select(p => new { p.Id, p.CreatedDate, p.Name })
                .AsNoTracking()
                .ToListAsync();

            var projectFilamentTotals = await _context.PrintFilament
                .Join(
                    _context.Prints.Where(pr => pr.CreatedById == userId && pr.ProjectId != null),
                    pf => pf.PrintId, pr => pr.Id,
                    (pf, pr) => new { pr.ProjectId, pf.AmountMg, pf.EstimatedAmountMg })
                .GroupBy(x => x.ProjectId)
                .Select(g => new
                {
                    ProjectId = g.Key,
                    TotalFilamentWeightMg = (long?)g.Sum(x =>
                        x.AmountMg > 0 ? (long?)x.AmountMg
                        : x.EstimatedAmountMg > 0 ? (long?)x.EstimatedAmountMg
                        : (long?)0) ?? 0L
                })
                .AsNoTracking()
                .ToListAsync();

            var projectFilamentLookup = projectFilamentTotals
                .Where(x => x.ProjectId.HasValue)
                .ToDictionary(x => x.ProjectId!.Value, x => x.TotalFilamentWeightMg);

            var projectSortKeys = projectList.Select(p => new FeedSortItem
            {
                Id = p.Id.ToString(),
                Type = "project",
                SortDate = new DateTimeOffset(DateTime.SpecifyKind(p.CreatedDate, DateTimeKind.Utc)),
                SortTitle = p.Name,
                TotalFilamentWeightMg = projectFilamentLookup.TryGetValue(p.Id, out var pw) ? pw : 0L
            }).ToList();

            // Standalone prints: same split to avoid correlated subquery per row.
            var standalonePrintList = await filteredPrintQuery
                .Where(p => p.ProjectId == null)
                .Select(p => new { p.Id, p.StartDate, p.CreatedDate, p.Title })
                .AsNoTracking()
                .ToListAsync();

            var standaloneFilamentTotals = await _context.PrintFilament
                .Join(
                    filteredPrintQuery.Where(p => p.ProjectId == null),
                    pf => pf.PrintId, pr => pr.Id,
                    (pf, pr) => new { pr.Id, pf.AmountMg, pf.EstimatedAmountMg })
                .GroupBy(x => x.Id)
                .Select(g => new
                {
                    PrintId = g.Key,
                    TotalFilamentWeightMg = (long?)g.Sum(x =>
                        x.AmountMg > 0 ? (long?)x.AmountMg
                        : x.EstimatedAmountMg > 0 ? (long?)x.EstimatedAmountMg
                        : (long?)0) ?? 0L
                })
                .AsNoTracking()
                .ToListAsync();

            var standaloneFilamentLookup = standaloneFilamentTotals
                .ToDictionary(x => x.PrintId, x => x.TotalFilamentWeightMg);

            var standaloneSortKeys = standalonePrintList.Select(p => new FeedSortItem
            {
                Id = p.Id.ToString(),
                Type = "print",
                SortDate = p.StartDate
                    ?? new DateTimeOffset(DateTime.SpecifyKind(p.CreatedDate, DateTimeKind.Utc)),
                SortTitle = p.Title,
                TotalFilamentWeightMg = standaloneFilamentLookup.TryGetValue(p.Id, out var spw) ? spw : 0L
            }).ToList();

            // ── Phase 4: Merge, sort, paginate the lightweight keys ───────────────────
            IEnumerable<FeedSortItem> merged = projectSortKeys.Concat(standaloneSortKeys);

            if (sortRequest?.SortColumn == PrintSummarySortColumn.Title)
            {
                merged = sortRequest.SortDirection == SortDirection.Asc
                    ? merged.OrderBy(x => x.SortTitle)
                    : merged.OrderByDescending(x => x.SortTitle);
            }
            else if (sortRequest?.SortColumn == PrintSummarySortColumn.FilamentUsage)
            {
                merged = sortRequest.SortDirection == SortDirection.Asc
                    ? merged.OrderBy(x => x.TotalFilamentWeightMg)
                    : merged.OrderByDescending(x => x.TotalFilamentWeightMg);
            }
            else
            {
                bool asc = sortRequest?.SortDirection == SortDirection.Asc;
                merged = asc
                    ? merged.OrderBy(x => x.SortDate)
                    : merged.OrderByDescending(x => x.SortDate);
            }

            var mergedList = merged.ToList();
            var total = mergedList.Count;
            var pagedKeys = mergedList
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            // ── Phase 5: Load full detail only for the current page's items ───────────
            var pageProjectGuids = pagedKeys
                .Where(x => x.Type == "project")
                .Select(x => Guid.Parse(x.Id))
                .ToList();

            var pagePrintIds = pagedKeys
                .Where(x => x.Type == "print")
                .Select(x => long.Parse(x.Id))
                .ToList();

            // — Project detail via targeted projections (avoids loading all prints per project) —
            Dictionary<Guid, Project> projectEntityLookup;
            Dictionary<Guid, (int PrintCount, int TotalPrintTime, int TotalEstPrintTime)> projectPrintStats;
            Dictionary<Guid, int> projectDefaultImageLookup;
            Dictionary<Guid, List<PrintFilamentSummaryDto>> projectFilamentUsageLookup;
            Dictionary<Guid, List<PrinterSummary>> projectPrinterLookup;

            if (pageProjectGuids.Count > 0)
            {
                var projectEntities = await _context.Projects
                    .Where(p => pageProjectGuids.Contains(p.Id))
                    .AsNoTracking()
                    .ToListAsync();
                projectEntityLookup = projectEntities.ToDictionary(p => p.Id);

                var printStatsRows = await _context.Prints
                    .Where(pr => pr.ProjectId != null && pageProjectGuids.Contains(pr.ProjectId.Value))
                    .GroupBy(pr => pr.ProjectId)
                    .Select(g => new
                    {
                        ProjectId = g.Key,
                        PrintCount = g.Count(),
                        TotalPrintTime = g.Sum(pr =>
                            pr.PrintTimeInSeconds != null && pr.PrintTimeInSeconds > 0
                                ? pr.PrintTimeInSeconds.Value
                                : (pr.EstimatedPrintTimeInSeconds ?? 0)),
                        TotalEstPrintTime = g.Sum(pr => pr.EstimatedPrintTimeInSeconds ?? 0)
                    })
                    .AsNoTracking()
                    .ToListAsync();
                projectPrintStats = printStatsRows
                    .Where(r => r.ProjectId.HasValue)
                    .ToDictionary(
                        r => r.ProjectId.Value,
                        r => (r.PrintCount, r.TotalPrintTime, r.TotalEstPrintTime));

                var defaultImageRows = await _context.ProjectImages
                    .Where(i => pageProjectGuids.Contains(i.ProjectId) && i.IsDefault)
                    .Select(i => new { i.ProjectId, i.Id })
                    .AsNoTracking()
                    .ToListAsync();
                projectDefaultImageLookup = defaultImageRows.ToDictionary(i => i.ProjectId, i => i.Id);

                var filamentUsageRows = await _context.PrintFilament
                    .Join(
                        _context.Prints.Where(pr => pr.ProjectId != null && pageProjectGuids.Contains(pr.ProjectId.Value)),
                        pf => pf.PrintId, pr => pr.Id,
                        (pf, pr) => new { pf.FilamentId, pr.ProjectId, pf.AmountMg, pf.EstimatedAmountMg })
                    .Where(x => x.FilamentId != null)
                    .GroupBy(x => new { x.ProjectId, x.FilamentId })
                    .Select(g => new
                    {
                        ProjectId = g.Key.ProjectId,
                        FilamentId = g.Key.FilamentId,
                        TotalAmountMg = (long?)g.Sum(x =>
                            x.AmountMg > 0 ? (long?)x.AmountMg
                            : x.EstimatedAmountMg > 0 ? (long?)x.EstimatedAmountMg
                            : (long?)0) ?? 0L
                    })
                    .AsNoTracking()
                    .ToListAsync();

                var uniqueFilamentIds = filamentUsageRows
                    .Where(r => r.FilamentId.HasValue)
                    .Select(r => r.FilamentId.Value)
                    .Distinct()
                    .ToList();
                var filamentEntities = uniqueFilamentIds.Count > 0
                    ? await _context.Filaments
                        .Where(f => uniqueFilamentIds.Contains(f.Id))
                        .Include(f => f.MaterialCategory)
                        .AsNoTracking()
                        .ToListAsync()
                    : new List<Filament>();
                var filamentEntityLookup = filamentEntities.ToDictionary(f => f.Id);
                projectFilamentUsageLookup = filamentUsageRows
                    .Where(r => r.ProjectId.HasValue && r.FilamentId.HasValue)
                    .GroupBy(r => r.ProjectId.Value)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(r => new PrintFilamentSummaryDto
                        {
                            Id = r.FilamentId.Value,
                            Filament = filamentEntityLookup.TryGetValue(r.FilamentId.Value, out var fil)
                                ? _mapper.Map<FilamentSummaryDto>(fil)
                                : null,
                            AmountMg = (int?)r.TotalAmountMg,
                            Source = PrintFilament.SourceMeasurement.Weight,
                        }).ToList());

                var printerMapRows = await _context.Prints
                    .Where(pr => pr.ProjectId != null && pageProjectGuids.Contains(pr.ProjectId.Value))
                    .GroupBy(pr => new { pr.ProjectId, pr.PrinterId })
                    .Select(g => new
                    {
                        g.Key.ProjectId,
                        g.Key.PrinterId,
                        PrintTimeInSeconds = (long)g.Sum(p => p.PrintTimeInSeconds ?? p.EstimatedPrintTimeInSeconds ?? 0)
                    })
                    .AsNoTracking()
                    .ToListAsync();
                var uniquePrinterIds = printerMapRows
                    .Where(r => r.ProjectId.HasValue)
                    .Select(r => r.PrinterId)
                    .Distinct()
                    .ToList();
                var printerEntities = uniquePrinterIds.Count > 0
                    ? await _context.Printers
                        .Where(pr => uniquePrinterIds.Contains(pr.Id))
                        .Include(pr => pr.Category)
                            .ThenInclude(c => c.MaterialCategory)
                        .AsNoTracking()
                        .ToListAsync()
                    : new List<Printer>();
                var printerDtoLookup = printerEntities.ToDictionary(
                    pr => pr.Id,
                    pr => _mapper.Map<PrinterSummary>(pr));
                projectPrinterLookup = printerMapRows
                    .Where(r => r.ProjectId.HasValue)
                    .GroupBy(r => r.ProjectId.Value)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(r =>
                              {
                                  if (!printerDtoLookup.TryGetValue(r.PrinterId, out var ps)) return null;
                                  return new PrinterSummary
                                  {
                                      Id = ps.Id,
                                      Name = ps.Name,
                                      Make = ps.Make,
                                      Model = ps.Model,
                                      IsActive = ps.IsActive,
                                      WattageW = ps.WattageW,
                                      Category = ps.Category,
                                      PrintTimeInSeconds = r.PrintTimeInSeconds
                                  };
                              })
                              .Where(ps => ps != null)
                              .ToList());
            }
            else
            {
                projectEntityLookup = new Dictionary<Guid, Project>();
                projectPrintStats = new Dictionary<Guid, (int, int, int)>();
                projectDefaultImageLookup = new Dictionary<Guid, int>();
                projectFilamentUsageLookup = new Dictionary<Guid, List<PrintFilamentSummaryDto>>();
                projectPrinterLookup = new Dictionary<Guid, List<PrinterSummary>>();
            }

            var pageStandalonePrints = pagePrintIds.Count > 0
                ? await _context.Prints
                    .Where(p => pagePrintIds.Contains(p.Id))
                    .Include(p => p.Printer)
                        .ThenInclude(pr => pr.Category)
                            .ThenInclude(c => c.MaterialCategory)
                    .Include(p => p.FilamentUsage)
                        .ThenInclude(pf => pf.Filament)
                            .ThenInclude(f => f.MaterialCategory)
                    .Include(p => p.Images)
                    .AsNoTracking()
                    .AsSplitQuery()
                    .ToListAsync()
                : new List<Print>();

            // ── Phase 6: Build DTOs in page-key order ─────────────────────────────────
            var printLookup = pageStandalonePrints.ToDictionary(p => p.Id);

            var pagedItems = pagedKeys.Select(key =>
            {
                if (key.Type == "project")
                {
                    if (!projectEntityLookup.TryGetValue(Guid.Parse(key.Id), out var p))
                        return null;

                    projectPrintStats.TryGetValue(p.Id, out var stats);
                    filteredGroupLookup.TryGetValue(p.Id, out var filteredCount);

                    return new GroupedFeedItemDto
                    {
                        Type = "project",
                        SortDate = key.SortDate,
                        ProjectId = p.Id,
                        ProjectName = p.Name,
                        ProjectReference = p.Reference,
                        ProjectStatus = p.Status,
                        PrintCount = stats.PrintCount,
                        FilteredPrintCount = hasFilters ? (int?)filteredCount : null,
                        TotalPrintTimeInSeconds = stats.TotalPrintTime,
                        TotalEstimatedPrintTimeInSeconds = stats.TotalEstPrintTime,
                        TotalFilamentWeightMg = projectFilamentLookup.TryGetValue(p.Id, out var fw) ? fw : 0L,
                        DefaultProjectImageId = projectDefaultImageLookup.TryGetValue(p.Id, out var imgId) ? imgId : 0,
                        FilamentUsage = projectFilamentUsageLookup.TryGetValue(p.Id, out var fu) ? fu : new List<PrintFilamentSummaryDto>(),
                        Printers = projectPrinterLookup.TryGetValue(p.Id, out var printers) ? printers : new List<PrinterSummary>(),
                    };
                }
                else
                {
                    if (!printLookup.TryGetValue(long.Parse(key.Id), out var p))
                        return null;
                    var sortDate = p.StartDate ?? new DateTimeOffset(DateTime.SpecifyKind(p.CreatedDate, DateTimeKind.Utc));
                    return new GroupedFeedItemDto
                    {
                        Type = "print",
                        SortDate = sortDate,
                        Print = _mapper.Map<PrintSummaryDTO>(p)
                    };
                }
            }).Where(item => item != null).ToList();

            return new PagedList<GroupedFeedItemDto>(pagedItems, total, pageNumber, pageSize);
        }
    }
}

