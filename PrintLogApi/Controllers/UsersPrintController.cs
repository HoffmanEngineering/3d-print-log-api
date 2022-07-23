using System;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Used to retrieve specific stats about a specific user.
    /// </summary>
    [Route("api/Users")]
    [ApiController]
    [Authorize]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client, NoStore = false)]
    public class UsersPrintsController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly IAuthorizationService _authorizationService;

        public UsersPrintsController(PrintLogContext context, IMapper mapper, IAuthorizationService authorizationService)
        {
            _context = context;
            _mapper = mapper;
            _authorizationService = authorizationService;

        }

        /// <summary>
        /// Get the total amount of filament used by a user between a date range.
        /// </summary>
        /// <param name="userId">The user Id to retrieve</param>
        /// <param name="fromDate">The datetime of the start of the date range (inclusive)</param>
        /// <param name="toDate">The datetime of the end of the date range (inclusive)</param>
        /// <returns></returns>
        [HttpGet("{userId}/total-filament-usage")]
        [AllowAnonymous]
        public async Task<ActionResult<SinglePrintStat>> GetUsersTotalFilamentUsage(long userId, [FromQuery] DateTimeOffset fromDate, [FromQuery] DateTimeOffset toDate)
        {
            var baseQuery = _context.Prints
                .Include(p => p.FilamentUsage)
                .Where(p => p.CreatedById == userId || p.Printer.UserId == userId)
                .Where(p => p.StartDate >= fromDate && p.StartDate <= toDate);

            // Calculate Filament Usage from the PrintFilaments
            var printFilamentUsage = await baseQuery
                .SelectMany(p => p.FilamentUsage.Select(pf => (long)((pf.AmountMg > 0 ? pf.AmountMg : null) ?? pf.EstimatedAmountMg ?? 0)))
                .SumAsync();

            // Calculate usage of the old "other" filament.
            var otherActualFilamentUsage = await baseQuery
                .Where(p => p.FilamentUsageMg.HasValue && p.FilamentUsageMg.Value > 0)
                .Select(p => (long?)p.FilamentUsageMg)
                .SumAsync();

            // Calculate the estimated usage from the old "other" filament.
            var otherEstimatedFilamentUsageWhenNoActualWasRecorded = await baseQuery
                .Where(p => (!p.FilamentUsageMg.HasValue || p.FilamentUsageMg.Value == 0) && p.EstimatedFilamentUsageMg.HasValue)
                .Select(p => (long?)p.EstimatedFilamentUsageMg)
                .SumAsync();

            var totalFilamentUsage = printFilamentUsage + (otherActualFilamentUsage ?? 0) + (otherEstimatedFilamentUsageWhenNoActualWasRecorded ?? 0);

            return new SinglePrintStat() { Stat = totalFilamentUsage.ToString() };
        }


        /// <summary>
        /// Get the total number of prints by a user between a date range.
        /// </summary>
        /// <param name="userId">The user Id to retrieve</param>
        /// <param name="fromDate">The datetime of the start of the date range (inclusive)</param>
        /// <param name="toDate">The datetime of the end of the date range (inclusive)</param>
        /// <returns></returns>
        [HttpGet("{userId}/print-count")]
        [AllowAnonymous]
        public async Task<ActionResult<SinglePrintStat>> GetUsersTotalPrintCount(long userId, [FromQuery] DateTimeOffset fromDate, [FromQuery] DateTimeOffset toDate)
        {
            var printCount = await _context.Prints
                .Where(p => p.CreatedById == userId || p.Printer.UserId == userId)
                .Where(p => p.StartDate >= fromDate && p.StartDate <= toDate)
                .CountAsync();


            return new SinglePrintStat() { Stat = printCount.ToString() };
        }


        /// <summary>
        /// Get the total print time by a user between a date range.
        /// </summary>
        /// <param name="userId">The user Id to retrieve</param>
        /// <param name="fromDate">The datetime of the start of the date range (inclusive)</param>
        /// <param name="toDate">The datetime of the end of the date range (inclusive)</param>
        /// <returns></returns>
        [HttpGet("{userId}/total-print-time")]
        [AllowAnonymous]
        public async Task<ActionResult<SinglePrintStat>> GetUsersTotalPrintTimeInSeconds(long userId, [FromQuery] DateTimeOffset fromDate, [FromQuery] DateTimeOffset toDate)
        {
            var printTime = await _context.Prints
                .Where(p => p.CreatedById == userId || p.Printer.UserId == userId)
                .Where(p => p.StartDate >= fromDate && p.StartDate <= toDate)
                .Where(p => p.PrintTimeInSeconds.HasValue || p.EstimatedPrintTimeInSeconds.HasValue)
                .Select(p => p.PrintTimeInSeconds.HasValue ? p.PrintTimeInSeconds : p.EstimatedPrintTimeInSeconds)
                .SumAsync();


            return new SinglePrintStat() { Stat = printTime.ToString() };
        }
    }
}
