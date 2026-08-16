using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using CsvHelper;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using PrintLogApi.Models.SortEnums;
using static PrintLogApi.Models.Print;
using static PrintLogApi.Services.MeasurementUtilities;

namespace PrintLogApi.Services
{
    public sealed class PrintService : IPrintService
    {

        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;
        private readonly IFilamentService _filamentService;
        private readonly IPrinterService _printerService;
        private readonly INotificationService _notificationService;
        private readonly ICacheVersionService _cacheVersionService;

        public PrintService(PrintLogContext context,
                            IMapper mapper,
                            TelemetryClient telemetry,
                            IFilamentService filamentService,
                            IPrinterService printerService,
                            INotificationService notificationService,
                            ICacheVersionService cacheVersionService)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
            _filamentService = filamentService;
            _printerService = printerService;
            _notificationService = notificationService;
            _cacheVersionService = cacheVersionService;
        }

        /// <summary>Maximum length of the free-text search term.</summary>
        public const int MaxSearchQueryLength = 200;

        public async Task<McpPage<PrintListItem>> SearchOwnPrintsForMcp(
            long userId, int page, int pageSize, PrintStatus? status, long? printerId,
            Guid? filamentId, DateTimeOffset? from, DateTimeOffset? to, string? searchQuery,
            CancellationToken ct)
        {
            var query = _context.Prints.AsNoTracking().Where(p => p.CreatedById == userId);

            if (searchQuery is not null && string.IsNullOrWhiteSpace(searchQuery))
            {
                throw McpToolException.InvalidArguments("query must not be empty.");
            }
            if (searchQuery is { Length: > MaxSearchQueryLength })
            {
                throw McpToolException.InvalidArguments(
                    $"query must be {MaxSearchQueryLength} characters or fewer.");
            }
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                // Substring, not word-boundary: users type partial names, so "bench" must find
                // "Dual Color 3D Benchy". Searching the project name too means a user who
                // remembers the project rather than the print can still find it.
                var term = searchQuery.Trim().ToLower();
                query = query.Where(p =>
                    p.Title!.ToLower().Contains(term)
                    // Ownership, not merely non-null: matching on a project the caller does not own
                    // would turn search_prints into an existence oracle for another user's project
                    // names (guess a name, see whether a hit comes back).
                    || (p.Project != null
                        && p.Project.CreatedById == userId
                        && p.Project.Name!.ToLower().Contains(term)));
            }

            if (status.HasValue)
            {
                query = query.Where(p => p.Status == status.Value);
            }
            if (printerId.HasValue)
            {
                query = query.Where(p => p.PrinterId == printerId.Value);
            }
            if (filamentId.HasValue)
            {
                query = query.Where(p => p.FilamentUsage!.Any(f => f.FilamentId == filamentId.Value));
            }
            if (from.HasValue)
            {
                query = query.Where(p => p.StartDate >= from.Value);
            }
            if (to.HasValue)
            {
                query = query.Where(p => p.StartDate <= to.Value);
            }

            var totalCount = await query.CountAsync(ct);

            var rows = await query
                .OrderByDescending(p => p.StartDate).ThenByDescending(p => p.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(p => new
                {
                    p.Id,
                    p.Title,
                    p.Status,
                    // Gate related names on ownership, not merely on non-null. A corrupt or imported
                    // row can point at another user's printer or project, and its NAME is their data.
                    // Same rule already applied to the filament rows below.
                    PrinterId = p.Printer != null && p.Printer.UserId == userId ? (long?)p.PrinterId : null,
                    PrinterName = p.Printer != null && p.Printer.UserId == userId ? p.Printer.Name : null,
                    p.StartDate,
                    // Canonical material usage: sum of per-filament actual weight, falling back to
                    // the estimated weight. The scalar Print.FilamentUsageMg is legacy and not
                    // maintained, so it must not be used. Mirrors PrintProfile / remaining-weight.
                    MaterialMg = p.FilamentUsage!.Sum(pf =>
                        pf.AmountMg.HasValue && pf.AmountMg > 0 ? pf.AmountMg.Value
                        : pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0 ? pf.EstimatedAmountMg.Value
                        : 0),
                    p.PrintTimeInSeconds,
                    p.EstimatedPrintTimeInSeconds,
                    // Material provenance: true when ANY contributing usage row fell back to its estimate.
                    MaterialIsEstimated = p.FilamentUsage!.Any(pf =>
                        !(pf.AmountMg.HasValue && pf.AmountMg > 0)
                        && pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0),
                    ProjectId = p.Project != null && p.Project.CreatedById == userId ? p.ProjectId : null,
                    ProjectName = p.Project != null && p.Project.CreatedById == userId ? p.Project.Name : null,
                })
                .ToListAsync(ct);

            var items = rows.Select(r =>
            {
                // In memory, so no EF constraint applies and the shared rule can be used directly.
                var seconds = PrintMetrics.Resolve(r.PrintTimeInSeconds, r.EstimatedPrintTimeInSeconds);
                return new PrintListItem(
                    r.Id,
                    r.Title!,
                    r.Status.ToString(),
                    r.PrinterId,
                    r.PrinterName,
                    r.StartDate,
                    McpUnits.MgToGrams(r.MaterialMg),
                    // Null, not 0, when nothing was recorded: a 0 would claim a measured zero.
                    seconds > 0 ? seconds : (int?)null,
                    PrintMetrics.IsEstimated(r.PrintTimeInSeconds, r.EstimatedPrintTimeInSeconds),
                    r.MaterialIsEstimated,
                    r.ProjectId,
                    r.ProjectName);
            }).ToList();

            var totalPages = pageSize > 0 ? (int)Math.Ceiling(totalCount / (double)pageSize) : 0;
            return new McpPage<PrintListItem>(items, page, pageSize, totalCount, totalPages);
        }

        /// <summary>
        /// Hard cap on the per-filament rows returned by get_print. Real prints are bounded by a
        /// printer's tool/AMS slots (single digits), so truncation signals bad data.
        /// </summary>
        public const int MaxMaterialsUsed = 100;

        public async Task<PrintDetailResult?> GetOwnPrintDetailForMcp(long userId, long printId, CancellationToken ct)
        {
            var row = await _context.Prints.AsNoTracking()
                .Where(p => p.Id == printId && p.CreatedById == userId)
                .Select(p => new
                {
                    p.Id,
                    p.Title,
                    p.Status,
                    // Ownership-gated for the same reason as the filament rows below: a cross-owner
                    // printer or project reference would leak that user's chosen names.
                    PrinterId = p.Printer != null && p.Printer.UserId == userId ? (long?)p.PrinterId : null,
                    PrinterName = p.Printer != null && p.Printer.UserId == userId ? p.Printer.Name : null,
                    p.StartDate,
                    MaterialMg = p.FilamentUsage!.Sum(pf =>
                        pf.AmountMg.HasValue && pf.AmountMg > 0 ? pf.AmountMg.Value
                        : pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0 ? pf.EstimatedAmountMg.Value
                        : 0),
                    p.PrintTimeInSeconds,
                    p.EstimatedPrintTimeInSeconds,
                    p.FileName,
                    p.Url,
                    p.ViewStatus,
                    p.AllowComments,
                    p.AllowFileDownloads,
                    p.Notes,
                    ProjectId = p.Project != null && p.Project.CreatedById == userId ? p.ProjectId : null,
                    ProjectName = p.Project != null && p.Project.CreatedById == userId ? p.Project.Name : null,

                    // Optional navigation => EF emits a LEFT JOIN, so rows with a NULL FilamentId
                    // are preserved. An inner join would silently drop them and the per-material
                    // rows would no longer add up to MaterialMg above.
                    // Not capped in SQL: EF cannot translate Take() inside a nested collection
                    // projection. That is acceptable here because this collection is bounded by one
                    // print's filament rows (a printer's tool/AMS slots — single digits), not by how
                    // much data the user has. MaxMaterialsUsed below is a safety net against bad
                    // data, not a paging mechanism.
                    Usage = p.FilamentUsage!
                        .OrderBy(pf => pf.Id)
                        .Select(pf => new
                        {
                            // Guard on ownership, not merely on non-null: a corrupt row can point at
                            // ANOTHER user's spool, and returning its brand/material/colour would
                            // leak their data. The quantity lives on the caller's own PrintFilament
                            // row, so it is safe to keep.
                            Readable = pf.Filament != null && pf.Filament.CreatedById == userId,
                            pf.FilamentId,
                            // Null-forgiven because this is an EF projection: the null nav is
                            // handled server-side in SQL and never dereferenced in process. The
                            // Readable flag above is what gates the values on the read side.
                            Name = pf.Filament!.DisplayName,
                            Brand = pf.Filament.Brand,
                            Material = pf.Filament.MaterialType,
                            Color = pf.Filament.ColorName,

                            // Identical to MaterialMg above and to McpStatisticsService: a zero or
                            // NEGATIVE actual falls through to the estimate. `AmountMg ?? Estimated`
                            // would not, and the sum invariant would break.
                            Mg = pf.AmountMg.HasValue && pf.AmountMg > 0 ? pf.AmountMg.Value
                                : pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0 ? pf.EstimatedAmountMg.Value
                                : 0,
                            IsEstimated = !(pf.AmountMg.HasValue && pf.AmountMg > 0)
                                && pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0,

                            // The unresolved figures, so a caller can see what was actually recorded
                            // vs. estimated. Same non-positive-means-unset rule as Mg above.
                            ActualMg = pf.AmountMg.HasValue && pf.AmountMg > 0 ? pf.AmountMg : (int?)null,
                            EstimatedMg = pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0 ? pf.EstimatedAmountMg : (int?)null,
                            pf.Notes,
                        })
                        .ToList(),
                })
                .FirstOrDefaultAsync(ct);

            if (row is null)
            {
                return null;
            }

            var truncated = row.Usage.Count > MaxMaterialsUsed;

            var materialsUsed = row.Usage
                .Take(MaxMaterialsUsed)
                .Select(u => new MaterialUsage(
                    u.Readable ? u.FilamentId : null,
                    u.Readable ? u.Name : null,
                    u.Readable ? u.Brand : null,
                    u.Readable ? u.Material : null,
                    u.Readable ? u.Color : null,
                    McpUnits.MgToGrams(u.Mg),
                    u.IsEstimated,
                    u.ActualMg.HasValue ? McpUnits.MgToGrams(u.ActualMg.Value) : (double?)null,
                    u.EstimatedMg.HasValue ? McpUnits.MgToGrams(u.EstimatedMg.Value) : (double?)null,
                    u.Notes))
                .ToList();

            var seconds = PrintMetrics.Resolve(row.PrintTimeInSeconds, row.EstimatedPrintTimeInSeconds);

            return new PrintDetailResult(
                row.Id, row.Title!, row.Status.ToString(), row.PrinterId, row.PrinterName,
                row.StartDate, McpUnits.MgToGrams(row.MaterialMg),
                // Null, not 0, when nothing was recorded: a 0 would claim a measured zero seconds.
                seconds > 0 ? seconds : (int?)null,
                PrintMetrics.IsEstimated(row.PrintTimeInSeconds, row.EstimatedPrintTimeInSeconds),
                // Over ALL usage rows, not just the ones that survived the MaxMaterialsUsed cap —
                // MaterialUsedGrams is computed over all of them too, so the flag must qualify the
                // same number.
                row.Usage.Any(u => u.IsEstimated),
                EstimatedCost: null, row.Notes, row.ProjectId, row.ProjectName,
                materialsUsed,
                truncated,
                materialsUsed.Sum(m => m.Grams),
                FileName: row.FileName, Url: row.Url, ViewStatus: row.ViewStatus.ToString(),
                EstimatedDurationSeconds: row.EstimatedPrintTimeInSeconds,
                AllowComments: row.AllowComments, AllowFileDownloads: row.AllowFileDownloads);
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
            string? searchText,
            SortRequest<PrintSummarySortColumn> sortRequest,
            IEnumerable<long>? filterByPrinterIds,
            IEnumerable<Guid>? filterByFilamentIds,
            IReadOnlyCollection<PrintStatus>? statuses,
            long? userId,
            long? currentUserId,
            IReadOnlyCollection<Guid>? projectIds = null,
            DateTimeOffset? fromDate = null,
            DateTimeOffset? toDate = null)
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
                    printQuery = printQuery.Where(p => p.Title!.Contains(text) || p.Notes!.Contains(text));
                }
            }

            // Half-open [fromDate, toDate) so adjacent windows never double-count a boundary
            // instant — the same rule the analytics endpoints use.
            if (fromDate.HasValue && toDate.HasValue)
            {
                printQuery = printQuery.Where(p => p.StartDate >= fromDate.Value && p.StartDate < toDate.Value);
            }

            if (statuses != null && statuses.Count > 0)
            {
                printQuery = printQuery.Where(p => statuses.Contains(p.Status));
            }

            // Filter by an of the selected printer ids.
            if (printerIdList != null && printerIdList.Any())
            {
                printQuery = printQuery.Where(p => printerIdList.Contains(p.PrinterId));
            }

            // Filter by any of the selected filament ids.
            if (filamentIdList != null && filamentIdList.Any())
            {
                printQuery = printQuery.Where(p => p.FilamentUsage!.Any(pf => pf.FilamentId.HasValue && filamentIdList.Contains((Guid)pf.FilamentId)));
            }

            if (projectIds != null && projectIds.Count > 0)
            {
                printQuery = printQuery.Where(p => p.ProjectId.HasValue && projectIds.Contains(p.ProjectId.Value));
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
                        printQuery = printQuery.OrderBy(src => src.FilamentUsage!.Sum(p => p.AmountMg.HasValue &&
                                                                                                    p.AmountMg > 0 ?
                                                                                                    p.AmountMg :
                                                                                                    p.EstimatedAmountMg.HasValue &&
                                                                                                    p.EstimatedAmountMg > 0 ?
                                                                                                    p.EstimatedAmountMg : 0)).ThenByDescending(p => p.CreatedDate);
                    }
                    else
                    {
                        printQuery = printQuery.OrderByDescending(src => src.FilamentUsage!.Sum(p => p.AmountMg.HasValue &&
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
                    .ThenInclude(pr => pr.Category!)
                        .ThenInclude(c => c.MaterialCategory)
                .Include(p => p.FilamentUsage!)
                    .ThenInclude(pf => pf.Filament!)
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

        public async Task<Print?> GetPrintById(long id)
        {
            var print = await this._context.Prints
                .Include(p => p.Printer)
                .Include(p => p.Images!)
                    .ThenInclude(p => p.File)
                .Include(p => p.Comments!)
                    .ThenInclude(p => p.Comment)
                .Include(p => p.FilamentUsage!)
                    .ThenInclude(pf => pf.Filament)
                .Where(p => p.Id == id)
                .AsSplitQuery()
                .FirstOrDefaultAsync();

            if (print is not null)
            {
                print.Comments = print.Comments!.OrderBy(c => c.CreatedDate).ThenBy(c => c.Id).ToList();
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

            foreach (var filament in newPrint.FilamentUsage!)
            {
                if (filament.FilamentId.HasValue && filament.FilamentId == default(Guid))
                    filament.FilamentId = null;
            }

            var filamentIdsToCheck = newPrint.FilamentUsage!
                .Select(f => f.FilamentId)
                .OfType<Guid>();

            if (!await _filamentService.CanUserAccessAllFilaments(userId, filamentIdsToCheck))
            {
                throw new UserCannotAccessFilamentException();
            }

            // The != default exclusion is redundant with the normalisation loop above, which has
            // already turned every empty GUID into a null. Kept anyway: it is free, and it stops
            // this list from silently admitting Guid.Empty if that loop ever moves.
            var newLoadedFilamentIds = newPrint.FilamentUsage!
                .Select(filament => filament.FilamentId)
                .OfType<Guid>()
                .Where(id => id != default);

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
            // Null-forgiven: the print was just persisted, so the re-read always finds it.
            return (await GetPrintById(newPrint.Id))!;
        }

        public async Task<CreatePrintResult> CreatePrintForMcp(
            long userId, string? title, long printerId, PrintStatus status,
            DateTimeOffset? startedAt, int? durationSeconds, int? estimatedDurationSeconds,
            string? notes, Guid? projectId, string? fileName, string? url,
            Print.PrintViewStatus? viewStatus, bool? allowComments, bool? allowFileDownloads,
            IReadOnlyList<MaterialUsageInput> materials, string idempotencyKey, CancellationToken ct)
        {
            const string toolName = "create_print";

            // Canonicalize ONCE, before both hashing and persistence. The fingerprint decides whether
            // two calls are "the same request", so anything it normalizes away must also be normalized
            // in what we store — otherwise the hash asserts an equivalence the stored row contradicts.
            title = title?.Trim();
            notes = notes?.Trim();
            fileName = fileName?.Trim();
            url = url?.Trim();
            materials = materials.Select(m => m with { Notes = m.Notes?.Trim() }).ToList();

            var fingerprint = McpRequestFingerprint.ComputeCreatePrint(
                title, printerId, status, startedAt, durationSeconds, estimatedDurationSeconds,
                notes, projectId, fileName, url, viewStatus, allowComments, allowFileDownloads, materials);

            var replay = await FindIdempotentPrint(userId, toolName, idempotencyKey, fingerprint, ct);
            if (replay != null)
            {
                return replay;
            }

            // Ownership checks. Foreign/missing ids all surface the same NotFound (no existence oracle).
            var printer = await _context.Printers
                .FirstOrDefaultAsync(p => p.Id == printerId && p.UserId == userId, ct);
            if (printer == null)
            {
                throw McpToolException.NotFound("Printer not found.");
            }

            if (projectId.HasValue &&
                !await _context.Projects.AnyAsync(p => p.Id == projectId.Value && p.CreatedById == userId, ct))
            {
                throw McpToolException.NotFound("Project not found.");
            }

            var materialIds = materials.Select(m => m.MaterialId).ToList();
            if (materialIds.Count != materialIds.Distinct().Count())
            {
                throw McpToolException.InvalidArguments("Each material may appear at most once in a print.");
            }
            if (materialIds.Count > 0 && !await _filamentService.CanUserAccessAllFilaments(userId, materialIds))
            {
                throw McpToolException.NotFound("Material not found.");
            }
            await RequireMcpConvertibleUsage(materials, userId, ct); // validate BEFORE building/persisting

            var newPrint = new Print
            {
                Title = title,
                Status = status,
                PrinterId = printerId,
                StartDate = startedAt,
                PrintTimeInSeconds = durationSeconds,
                EstimatedPrintTimeInSeconds = estimatedDurationSeconds,
                Notes = notes,
                FileName = fileName,
                Url = url,
                ProjectId = projectId,
                CreatedById = userId,
                UpdatedById = userId,
                FilamentUsage = materials.Select(ToPrintFilament).ToList(),
            };

            await ApplyMcpPrintDefaults(newPrint, viewStatus, allowComments, allowFileDownloads, userId, ct);
            await UpdateFilamentUsageWeights(newPrint);

            try
            {
                // SqlServerRetryingExecutionStrategy forbids user-initiated transactions unless they
                // run inside an execution strategy, so the whole tx is the retriable unit.
                var strategy = _context.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    using var tx = await _context.Database.BeginTransactionAsync(ct);
                    _context.Prints.Add(newPrint);
                    await _context.SaveChangesAsync(ct);

                    _context.McpIdempotencyRecords.Add(
                        McpIdempotencyRecordFactory.ForPrint(userId, idempotencyKey, fingerprint, newPrint.Id));
                    await _context.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                });
            }
            catch (DbUpdateException)
            {
                // Possible unique-index race: another identical call created the print first. The
                // transaction has rolled back but the failed Added entities are still tracked; clear
                // them so the recovery query reads only committed state, then replay the winner's
                // result. If there is no such record the failure was something else, so rethrow.
                _context.ChangeTracker.Clear();
                // Fingerprint match -> replay the winner's result; a mismatch throws conflict inside.
                var concurrent = await FindIdempotentPrint(userId, toolName, idempotencyKey, fingerprint, ct);
                if (concurrent != null)
                {
                    return concurrent;
                }
                throw;
            }

            _cacheVersionService.InvalidateUserCache(userId);
            return await BuildCreatePrintResult(newPrint.Id, wasReplayed: false, userId, ct);
        }

        private async Task<CreatePrintResult?> FindIdempotentPrint(
            long userId, string toolName, string key, string fingerprint, CancellationToken ct)
        {
            var record = await _context.McpIdempotencyRecords
                .FirstOrDefaultAsync(r => r.UserId == userId && r.ToolName == toolName && r.IdempotencyKey == key, ct);
            if (record == null)
            {
                return null;
            }

            // A key reused with a DIFFERENT payload is a caller bug, not a retry: replaying the old
            // print would silently discard the new arguments. A null fingerprint is a legacy record
            // with no stored payload to compare, so it replays unconditionally.
            if (record.RequestFingerprint != null && record.RequestFingerprint != fingerprint)
            {
                throw McpToolException.Conflict("This idempotency key was already used with different arguments.");
            }

            // The record is scoped by ToolName, so a create_print row always carries a print id. A
            // null here means the row is corrupt, not that another tool owns it — treat it exactly
            // like a dangling reference rather than dereferencing it.
            // Short-circuit order is load-bearing: the null test stays on the left so the query is
            // still skipped entirely when the id is absent.
            if (record.CreatedPrintId is not { } createdPrintId
                || !await _context.Prints.AnyAsync(p => p.Id == createdPrintId && p.CreatedById == userId, ct))
            {
                throw McpToolException.NotFound("The prior result for this idempotency key no longer exists.");
            }

            return await BuildCreatePrintResult(createdPrintId, wasReplayed: true, userId, ct);
        }

        /// <summary>
        /// Builds the entity row from an input row. Source/EstimatedSource are non-nullable enums on
        /// the entity, so each is assigned only when its input pair is present; otherwise the entity
        /// default (0) stands and the paired amount fields stay null, which downstream code reads as
        /// "unset". Length/Volume overflow safety is enforced on the input rows by
        /// RequireMcpConvertibleUsage, which runs before this in the create/update flow.
        /// </summary>
        private static PrintFilament ToPrintFilament(MaterialUsageInput m)
        {
            var pf = new PrintFilament { FilamentId = m.MaterialId, Notes = m.Notes };

            if (m.Source.HasValue && m.Amount.HasValue)
            {
                pf.Source = (PrintFilament.SourceMeasurement)(int)m.Source.Value;
                switch (m.Source.Value)
                {
                    case McpMeasurementSource.Weight:
                        pf.AmountMg = checked((int)Math.Round(m.Amount.Value * 1000.0)); // g -> mg
                        break;
                    case McpMeasurementSource.Length:
                        pf.LengthInM = m.Amount.Value / 1000.0; // mm -> m
                        break;
                    case McpMeasurementSource.Volume:
                        pf.VolumeMl = m.Amount.Value; // ml
                        break;
                }
            }

            if (m.EstimatedSource.HasValue && m.EstimatedAmount.HasValue)
            {
                pf.EstimatedSource = (PrintFilament.SourceMeasurement)(int)m.EstimatedSource.Value;
                switch (m.EstimatedSource.Value)
                {
                    case McpMeasurementSource.Weight:
                        pf.EstimatedAmountMg = checked((int)Math.Round(m.EstimatedAmount.Value * 1000.0));
                        break;
                    case McpMeasurementSource.Length:
                        pf.EstimatedLengthInM = m.EstimatedAmount.Value / 1000.0; // mm -> m
                        break;
                    case McpMeasurementSource.Volume:
                        pf.EstimatedVolumeMl = m.EstimatedAmount.Value; // ml
                        break;
                }
            }

            return pf;
        }

        /// <summary>
        /// MCP-only pre-persist convertibility guard, validated on the INPUT rows (native units:
        /// Weight=g, Length=mm, Volume=ml). Requirement per source: Weight=none; Volume=density;
        /// Length=density+diameter (finite &amp; &gt; 0). Converted milligrams must be finite and in
        /// (0, int.MaxValue]. Rejects with invalid_arguments instead of overflowing or throwing.
        /// </summary>
        private async Task RequireMcpConvertibleUsage(
            IReadOnlyList<MaterialUsageInput> rows, long userId, CancellationToken ct)
        {
            var ids = rows.Select(r => r.MaterialId).Distinct().ToList();
            if (ids.Count == 0)
            {
                return;
            }

            var map = await _context.Filaments
                .Where(f => ids.Contains(f.Id))
                .Include(f => f.MaterialCategory)
                .AsNoTracking()
                .ToDictionaryAsync(f => f.Id, ct);

            foreach (var row in rows)
            {
                if (!map.TryGetValue(row.MaterialId, out var f))
                {
                    // Ownership/existence is enforced separately; a missing map entry means the row
                    // will already have been rejected as not_found. Fail closed here regardless.
                    throw McpToolException.NotFound("Material not found.");
                }
                // Restated here rather than relied upon from PrintLogWriteTools.ValidateUsageRow:
                // that runs in the tool layer, and this service is reachable without it. Without
                // this, a half-populated pair reaches the dereferences below and throws
                // InvalidOperationException instead of reporting invalid_arguments.
                if (row.Source.HasValue != row.Amount.HasValue)
                {
                    throw McpToolException.InvalidArguments("A material row's source and amount must be provided together.");
                }
                if (row.EstimatedSource.HasValue != row.EstimatedAmount.HasValue)
                {
                    throw McpToolException.InvalidArguments("A material row's estimatedSource and estimatedAmount must be provided together.");
                }
                if (row.Source is { } source && row.Amount is { } amount)
                {
                    RequireConvertible(source, amount, f);
                }
                if (row.EstimatedSource is { } estimatedSource && row.EstimatedAmount is { } estimatedAmount)
                {
                    RequireConvertible(estimatedSource, estimatedAmount, f);
                }
            }
        }

        private static void RequireConvertible(McpMeasurementSource source, double amountInSourceUnit, Filament f)
        {
            bool requiresDensity = source != McpMeasurementSource.Weight;
            bool requiresDiameter = source == McpMeasurementSource.Length;

            if (requiresDensity && !(double.IsFinite(f.MaterialDensityGramPerCubicCm) && f.MaterialDensityGramPerCubicCm > 0))
            {
                throw McpToolException.InvalidArguments(
                    "This material cannot record usage measured by that unit (missing density).");
            }
            if (requiresDiameter && !(f.DiameterMm.HasValue && double.IsFinite(f.DiameterMm.Value) && f.DiameterMm.Value > 0))
            {
                throw McpToolException.InvalidArguments(
                    "This material cannot record usage measured by length (missing diameter).");
            }

            // Must apply the SAME rounding the persistence path applies, or a positive amount that
            // rounds to 0 mg passes validation and is then stored as zero — which every read path
            // treats as "unset" and silently replaces with the estimate. The Length/Volume helpers
            // already round internally (matching UpdateFilamentUsageWeights); Weight is rounded here
            // exactly as ToPrintFilament rounds it.
            double mg = source switch
            {
                // amountInSourceUnit is mm here; GetAmountMgFromLength expects meters -> divide once.
                // requiresDiameter is exactly `source == Length`, so the guard above already threw
                // unless DiameterMm was present. Stated as a throwing fallback rather than a `when`
                // clause: a `when` would fall through to the Weight arm below and convert
                // millimetres as if they were grams, which is worse than failing.
                McpMeasurementSource.Length => GetAmountMgFromLength(
                    amountInSourceUnit / 1000.0,
                    f.DiameterMm ?? throw new InvalidOperationException(
                        "A Length source reached conversion without a diameter; the guard above should have rejected it."),
                    f.MaterialDensityGramPerCubicCm),
                McpMeasurementSource.Volume => GetAmountMgFromVolume(
                    amountInSourceUnit, f.MaterialDensityGramPerCubicCm),
                _ => Math.Round(amountInSourceUnit * 1000.0), // g -> mg
            };

            // 1 mg is the smallest recordable amount: below that the column cannot represent it.
            if (!double.IsFinite(mg) || mg < 1 || mg > int.MaxValue)
            {
                throw McpToolException.InvalidArguments("A material usage amount is out of the recordable range.");
            }
        }

        /// <summary>
        /// Applies the caller's explicit visibility/social choices, falling back to the user's saved
        /// defaults and finally to the safe defaults (Private, no comments, no downloads). A stored
        /// setting that is malformed — or numerically parseable but not a DEFINED enum member — falls
        /// back rather than persisting a nonsense visibility.
        /// </summary>
        private async Task ApplyMcpPrintDefaults(
            Print print, Print.PrintViewStatus? viewStatus, bool? allowComments, bool? allowFileDownloads,
            long userId, CancellationToken ct)
        {
            const int defaultViewStatusTypeId = 1;   // Prints_DefaultPrintViewStatus
            const int lastAllowCommentsTypeId = 3;   // Prints_LastSelectedAllowComments

            if (viewStatus.HasValue)
            {
                print.ViewStatus = viewStatus.Value;
            }
            else
            {
                print.ViewStatus = Print.PrintViewStatus.Private;
                var s = await _context.UserSettings.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.UserId == userId && u.UserSettingTypeId == defaultViewStatusTypeId, ct);
                if (s?.Value is { } v && Enum.TryParse<Print.PrintViewStatus>(v, out var parsed) && Enum.IsDefined(parsed))
                {
                    print.ViewStatus = parsed;
                }
            }

            if (allowComments.HasValue)
            {
                print.AllowComments = allowComments.Value;
            }
            else
            {
                print.AllowComments = false;
                var s = await _context.UserSettings.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.UserId == userId && u.UserSettingTypeId == lastAllowCommentsTypeId, ct);
                if (s?.Value is { } v && bool.TryParse(v, out var parsed))
                {
                    print.AllowComments = parsed;
                }
            }

            print.AllowFileDownloads = allowFileDownloads ?? false;
        }

        private async Task<CreatePrintResult> BuildCreatePrintResult(
            long printId, bool wasReplayed, long userId, CancellationToken ct)
        {
            var detail = await GetOwnPrintDetailForMcp(userId, printId, ct)
                ?? throw McpToolException.NotFound("Print not found.");
            return new CreatePrintResult(detail, wasReplayed, await BuildMaterialRemaining(printId, userId, ct));
        }

        private async Task<IReadOnlyList<MaterialRemaining>> BuildMaterialRemaining(
            long printId, long userId, CancellationToken ct)
        {
            var materialIds = await _context.PrintFilament.AsNoTracking()
                .Where(pf => pf.PrintId == printId && pf.FilamentId.HasValue)
                // Guarded by the Where above. This is an EF expression tree translated to SQL and
                // never dereferenced in process, so ! is the only permitted fix here - an OfType
                // or pattern rewrite would change the translation.
                .Select(pf => pf.FilamentId!.Value)
                .Distinct()
                .ToListAsync(ct);

            var remaining = new List<MaterialRemaining>();
            foreach (var id in materialIds)
            {
                remaining.Add(new MaterialRemaining(id, await _filamentService.GetRemainingGramsForMcp(userId, id, ct)));
            }
            return remaining;
        }

        public async Task<PrintDetailResult> UpdateOwnPrintForMcp(
            long userId, long printId, string? title, PrintStatus? status, string? notes, DateTimeOffset? startedAt,
            long? printerId, int? durationSeconds, int? estimatedDurationSeconds, string? fileName, string? url,
            Print.PrintViewStatus? viewStatus, bool? allowComments, bool? allowFileDownloads,
            Guid? projectId, bool materialsProvided, IReadOnlyList<MaterialUsageInput>? materials,
            ISet<string> clearFields, CancellationToken ct)
        {
            var print = await _context.Prints
                .Include(p => p.FilamentUsage)
                .FirstOrDefaultAsync(p => p.Id == printId && p.CreatedById == userId, ct);
            if (print == null)
            {
                throw McpToolException.NotFound("Print not found.");
            }

            // Canonicalize to the same form create_print stores, so a field's value does not depend on
            // which tool last wrote it. materials stays null when the caller omitted it entirely.
            title = title?.Trim();
            notes = notes?.Trim();
            fileName = fileName?.Trim();
            url = url?.Trim();
            materials = materials?.Select(m => m with { Notes = m.Notes?.Trim() }).ToList();

            // ---- Validate everything first: a rejected edit must leave the print untouched. ----
            void Guard(string field, bool isSet)
            {
                if (isSet && clearFields.Contains(field))
                {
                    throw McpToolException.InvalidArguments($"'{field}' cannot be both set and cleared.");
                }
            }
            Guard("fileName", fileName != null);
            Guard("url", url != null);
            Guard("notes", notes != null);
            Guard("startedAt", startedAt.HasValue);
            Guard("durationSeconds", durationSeconds.HasValue);
            Guard("estimatedDurationSeconds", estimatedDurationSeconds.HasValue);
            Guard("projectId", projectId.HasValue);

            if (printerId.HasValue &&
                !await _context.Printers.AnyAsync(p => p.Id == printerId.Value && p.UserId == userId, ct))
            {
                throw McpToolException.NotFound("Printer not found.");
            }
            if (projectId.HasValue &&
                !await _context.Projects.AnyAsync(p => p.Id == projectId.Value && p.CreatedById == userId, ct))
            {
                throw McpToolException.NotFound("Project not found.");
            }
            if (materialsProvided)
            {
                // materialsProvided is the caller's assertion that materials is populated; flow
                // analysis cannot relate the two parameters. Both dereferences of `materials` in
                // this method sit behind that flag.
                var mids = materials!.Select(m => m.MaterialId).ToList();
                if (mids.Count != mids.Distinct().Count())
                {
                    throw McpToolException.InvalidArguments("Each material may appear at most once.");
                }
                if (mids.Count > 0 && !await _filamentService.CanUserAccessAllFilaments(userId, mids))
                {
                    throw McpToolException.NotFound("Material not found.");
                }
                await RequireMcpConvertibleUsage(materials!, userId, ct);
            }

            // ---- Mutate. ----
            if (title != null)
            {
                print.Title = title;
            }
            if (status.HasValue)
            {
                print.Status = status.Value;
            }
            if (viewStatus.HasValue)
            {
                print.ViewStatus = viewStatus.Value;
            }
            if (allowComments.HasValue)
            {
                print.AllowComments = allowComments.Value;
            }
            if (allowFileDownloads.HasValue)
            {
                print.AllowFileDownloads = allowFileDownloads.Value;
            }
            if (printerId.HasValue)
            {
                print.PrinterId = printerId.Value;
            }

            if (notes != null) { print.Notes = notes; } else if (clearFields.Contains("notes")) { print.Notes = null; }
            if (fileName != null) { print.FileName = fileName; } else if (clearFields.Contains("fileName")) { print.FileName = null; }
            if (url != null) { print.Url = url; } else if (clearFields.Contains("url")) { print.Url = null; }
            if (startedAt.HasValue) { print.StartDate = startedAt; } else if (clearFields.Contains("startedAt")) { print.StartDate = null; }
            if (durationSeconds.HasValue) { print.PrintTimeInSeconds = durationSeconds; } else if (clearFields.Contains("durationSeconds")) { print.PrintTimeInSeconds = null; }
            if (estimatedDurationSeconds.HasValue) { print.EstimatedPrintTimeInSeconds = estimatedDurationSeconds; } else if (clearFields.Contains("estimatedDurationSeconds")) { print.EstimatedPrintTimeInSeconds = null; }
            if (projectId.HasValue) { print.ProjectId = projectId; } else if (clearFields.Contains("projectId")) { print.ProjectId = null; }

            if (materialsProvided)
            {
                _context.PrintFilament.RemoveRange(print.FilamentUsage!);
                print.FilamentUsage = materials!.Select(ToPrintFilament).ToList();
                await UpdateFilamentUsageWeights(print);
            }

            print.UpdatedById = userId;
            await _context.SaveChangesAsync(ct);
            _cacheVersionService.InvalidateUserCache(userId);

            return await GetOwnPrintDetailForMcp(userId, printId, ct)
                ?? throw McpToolException.NotFound("Print not found.");
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
            updatedPrint.Printer = printer!;

            // Check if the user had access to that printer!
            // Null-forgiven: an unknown PrinterId already threw here before nullable analysis was
            // enabled, and it fails closed either way. Returning a clean not-found instead is a
            // behaviour change, tracked in #57.
            if (userId != printer!.UserId)
            {
                //return BadRequest();
                throw new UserCannotAccessPrinterException();
            }

            foreach (var filament in updatedPrint.FilamentUsage!)
            {
                if (filament.FilamentId.HasValue && filament.FilamentId == default(Guid))
                    filament.FilamentId = null;
            }

            var updatedFilamentIdsToCheck = updatedPrint.FilamentUsage!
                .Select(f => f.FilamentId)
                .OfType<Guid>();

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

            // Null-forgiven: the print was just persisted, so the re-read always finds it.
            return (await GetPrintById(updatedPrint.Id))!;
        }

        /// <summary>
        /// When we save the filament usage, we need to ensure that the filament weights, lengths and volume are correctly filled out.
        /// </summary>
        public async Task UpdateFilamentUsageWeights(Print print)
        {
            // OfType unwraps first so the != default test below compares a plain Guid and needs no
            // .Value. The empty-GUID exclusion is kept deliberately: it is belt-and-braces with the
            // identical guard at the top of the loop below, and dropping it here would make this
            // list depend on that one staying put.
            var filamentIds = print.FilamentUsage!
                .Select(pf => pf.FilamentId)
                .OfType<Guid>()
                .Where(id => id != default)
                .Distinct()
                .ToList();

            if (filamentIds.Count == 0) return;

            var filamentMap = await _context.Filaments
                .Where(f => filamentIds.Contains(f.Id))
                .Include(f => f.MaterialCategory)
                .AsNoTracking()
                .ToDictionaryAsync(f => f.Id);

            foreach (var pf in print.FilamentUsage!)
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
                    // The diameter test is what the Volume and Weight branches below already do.
                    // Without it a material whose category tracks no diameter (resin, powder)
                    // reaches DiameterMm.Value and throws.
                    if (pf.LengthInM.HasValue && hasDiameter && filament.DiameterMm is { } diameterMm)
                    {
                        pf.AmountMg = (int)GetAmountMgFromLength(pf.LengthInM.Value, diameterMm, filament.MaterialDensityGramPerCubicCm);
                        pf.VolumeMl = GetVolumeInMlFromLengthM(pf.LengthInM.Value, diameterMm);
                    }
                }
                else if (pf.Source == PrintFilament.SourceMeasurement.Volume)
                {
                    if (pf.VolumeMl.HasValue)
                    {
                        pf.AmountMg = (int)GetAmountMgFromVolume(pf.VolumeMl.Value, filament.MaterialDensityGramPerCubicCm);

                        if (hasDiameter && filament.DiameterMm is { } diameterMm)
                        {
                            pf.LengthInM = GetLengthInMetersFromVolume(pf.VolumeMl.Value, diameterMm);
                        }
                    }
                }
                else
                {
                    if (pf.AmountMg.HasValue)
                    {
                        pf.VolumeMl = GetVolumeInMlFromAmount(pf.AmountMg.Value, filament.MaterialDensityGramPerCubicCm);

                        if (hasDiameter && filament.DiameterMm is { } diameterMm)
                        {
                            pf.LengthInM = GetLengthInMetersFromAmount(pf.AmountMg.Value, diameterMm, filament.MaterialDensityGramPerCubicCm);
                        }
                    }
                }

                if (pf.EstimatedSource == PrintFilament.SourceMeasurement.Length)
                {
                    // Same missing diameter test as the actual-measurement Length branch above.
                    if (pf.EstimatedLengthInM.HasValue && hasDiameter && filament.DiameterMm is { } diameterMm)
                    {
                        pf.EstimatedAmountMg = (int)GetAmountMgFromLength(pf.EstimatedLengthInM.Value, diameterMm, filament.MaterialDensityGramPerCubicCm);
                        pf.EstimatedVolumeMl = GetVolumeInMlFromLengthM(pf.EstimatedLengthInM.Value, diameterMm);
                    }
                }
                else if (pf.EstimatedSource == PrintFilament.SourceMeasurement.Volume)
                {
                    if (pf.EstimatedVolumeMl.HasValue)
                    {
                        pf.EstimatedAmountMg = (int)GetAmountMgFromVolume(pf.EstimatedVolumeMl.Value, filament.MaterialDensityGramPerCubicCm);

                        if (hasDiameter && filament.DiameterMm is { } diameterMm)
                        {
                            pf.EstimatedLengthInM = GetLengthInMetersFromVolume(pf.EstimatedVolumeMl.Value, diameterMm);
                        }
                    }
                }
                else
                {
                    if (pf.EstimatedAmountMg.HasValue)
                    {
                        pf.EstimatedVolumeMl = GetVolumeInMlFromAmount(pf.EstimatedAmountMg.Value, filament.MaterialDensityGramPerCubicCm);

                        if (hasDiameter && filament.DiameterMm is { } diameterMm)
                        {
                            pf.EstimatedLengthInM = GetLengthInMetersFromAmount(pf.EstimatedAmountMg.Value, diameterMm, filament.MaterialDensityGramPerCubicCm);
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
            // Null-forgiven: an unknown image id already threw here. Note the print existence
            // check above throws ArgumentNullException but the image is not validated — tracked
            // in #57 rather than changed in this annotation-only pass.
            selectedImage!.IsDefault = true;

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
            foreach (var comment in print.Comments!.ToArray())
            {
                _context.Comments.Remove(comment.Comment);
            }
            _context.PrintComments.RemoveRange(print.Comments!.ToArray());

            // Remove Print Images.
            foreach (var image in print.Images!.ToArray())
            {
                _context.Files.Remove(image.File);
            }
            _context.PrintImages.RemoveRange(print.Images!.ToArray());

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
            _context.PrintFilament.RemoveRange(print.FilamentUsage!.ToArray());

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

        private enum FeedItemType { Print, Project }

        private sealed class FeedSortItem
        {
            public FeedItemType Type { get; init; }
            public long? PrintId { get; init; }
            public Guid? ProjectId { get; init; }
            public DateTimeOffset SortDate { get; init; }
            public string? SortTitle { get; init; }
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
            string? searchText = null,
            IEnumerable<long>? filterByPrinterIds = null,
            IEnumerable<Guid>? filterByFilamentIds = null,
            Print.PrintStatus? filterByStatus = null,
            SortRequest<PrintSummarySortColumn>? sortRequest = null)
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
                    filteredPrintQuery = filteredPrintQuery.Where(p => p.Title!.Contains(text) || p.Notes!.Contains(text) || p.Project!.Name!.Contains(text));
            }

            if (filterByStatus.HasValue)
                filteredPrintQuery = filteredPrintQuery.Where(p => p.Status == filterByStatus.Value);

            if (printerIdList != null && printerIdList.Any())
                filteredPrintQuery = filteredPrintQuery.Where(p => printerIdList.Contains(p.PrinterId));

            if (filamentIdList != null && filamentIdList.Any())
            {
                filteredPrintQuery = filteredPrintQuery.Where(p =>
                    p.FilamentUsage!.Any(pf => pf.FilamentId.HasValue && filamentIdList.Contains((Guid)pf.FilamentId)));
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
                    // Non-null by the Where; the group is still needed for the value selector, so
                    // a Select+OfType unwrap here would discard FilteredPrintCount.
                    .Where(g => g.ProjectId.HasValue)
                    .ToDictionary(g => g.ProjectId!.Value, g => g.FilteredPrintCount);
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
                Type = FeedItemType.Project,
                ProjectId = p.Id,
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
                Type = FeedItemType.Print,
                PrintId = p.Id,
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
                .Where(x => x.Type == FeedItemType.Project)
                .Select(x => x.ProjectId!.Value)
                .ToList();

            var pagePrintIds = pagedKeys
                .Where(x => x.Type == FeedItemType.Print)
                .Select(x => x.PrintId!.Value)
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
                        // Already the correct fallback; the estimate just needed the same > 0 guard,
                        // so a corrupt negative estimate cannot subtract from the total.
                        TotalPrintTime = g.Sum(pr =>
                            pr.PrintTimeInSeconds.HasValue && pr.PrintTimeInSeconds > 0
                                ? pr.PrintTimeInSeconds.Value
                                : pr.EstimatedPrintTimeInSeconds.HasValue && pr.EstimatedPrintTimeInSeconds > 0
                                    ? pr.EstimatedPrintTimeInSeconds.Value
                                    : 0),
                        // Deliberately estimate-only: it answers "what did the slicer predict?", a
                        // DIFFERENT question from TotalPrintTime. Do not turn this into a resolved
                        // value. Guarded > 0 only.
                        TotalEstPrintTime = g.Sum(pr =>
                            pr.EstimatedPrintTimeInSeconds.HasValue && pr.EstimatedPrintTimeInSeconds > 0
                                ? pr.EstimatedPrintTimeInSeconds.Value
                                : 0)
                    })
                    .AsNoTracking()
                    .ToListAsync();
                projectPrintStats = printStatsRows
                    // Non-null by the Where; the row is still needed for the value selector, so
                    // a Select+OfType unwrap here would discard the three statistics.
                    .Where(r => r.ProjectId.HasValue)
                    .ToDictionary(
                        r => r.ProjectId!.Value,
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
                    .Select(r => r.FilamentId)
                    .OfType<Guid>()
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
                    // Both ids are proved once, up front, so nothing below needs .Value. A Where
                    // cannot do this job: it proves TWO members at a time, and flow analysis does
                    // not carry either of them into the lambdas that follow.
                    .SelectMany(r => r.ProjectId is { } projectId && r.FilamentId is { } filamentId
                        ? new[] { (ProjectId: projectId, FilamentId: filamentId, r.TotalAmountMg) }
                        : Array.Empty<(Guid ProjectId, Guid FilamentId, long TotalAmountMg)>())
                    .GroupBy(r => r.ProjectId)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(r => new PrintFilamentSummaryDto
                        {
                            Id = r.FilamentId,
                            Filament = filamentEntityLookup.TryGetValue(r.FilamentId, out var fil)
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
                        // `?? Estimated ?? 0` is defeated by a stored 0: 0.HasValue is true, so the
                        // webhook's coerced zero would win and suppress a perfectly good estimate.
                        //
                        // The cast is INSIDE the Sum, not around it: `(long)g.Sum(int)` sums in int
                        // and only widens the result, so SQL Server's SUM(int) would overflow before
                        // the conversion could help. Summing longs emits SUM(bigint).
                        PrintTimeInSeconds = g.Sum(p => (long)(
                            p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0 ? p.PrintTimeInSeconds.Value
                            : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0 ? p.EstimatedPrintTimeInSeconds.Value
                            : 0))
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
                        .Include(pr => pr.Category!)
                            .ThenInclude(c => c.MaterialCategory)
                        .AsNoTracking()
                        .ToListAsync()
                    : new List<Printer>();
                var printerDtoLookup = printerEntities.ToDictionary(
                    pr => pr.Id,
                    pr => _mapper.Map<PrinterSummary>(pr));
                projectPrinterLookup = printerMapRows
                    // Non-null by the Where; the row is still needed inside the group projection
                    // below, so a Select+OfType unwrap here would discard PrinterId.
                    .Where(r => r.ProjectId.HasValue)
                    .GroupBy(r => r.ProjectId!.Value)
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
                              // Restates the element type after the null filter above; the
                              // Where already guarantees it, but flow analysis cannot see that.
                              .Select(ps => ps!)
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
                        .ThenInclude(pr => pr.Category!)
                            .ThenInclude(c => c.MaterialCategory)
                    .Include(p => p.FilamentUsage!)
                        .ThenInclude(pf => pf.Filament!)
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
                if (key.Type == FeedItemType.Project)
                {
                    if (!projectEntityLookup.TryGetValue(key.ProjectId!.Value, out var p))
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
                    if (!printLookup.TryGetValue(key.PrintId!.Value, out var p))
                        return null;
                    var sortDate = p.StartDate ?? new DateTimeOffset(DateTime.SpecifyKind(p.CreatedDate, DateTimeKind.Utc));
                    return new GroupedFeedItemDto
                    {
                        Type = "print",
                        SortDate = sortDate,
                        Print = _mapper.Map<PrintSummaryDTO>(p)
                    };
                }
            }).Where(item => item != null).Select(item => item!).ToList();

            return new PagedList<GroupedFeedItemDto>(pagedItems, total, pageNumber, pageSize);
        }
    }
}

