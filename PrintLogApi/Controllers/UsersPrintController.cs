using AutoMapper;
using AutoMapper.QueryableExtensions;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.User;
using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

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

        [HttpGet("{userId}/total-filament-usage")]
        [AllowAnonymous]
        public async Task<ActionResult<SinglePrintStat>> GetUsersTotalFilamentUsage(long userId, [FromQuery] DateTimeOffset fromDate, [FromQuery] DateTimeOffset toDate)
        {
            var baseQuery = _context.Prints
                .Where(p => p.CreatedById == userId || p.Printer.UserId == userId)
                .Where(p => p.StartDate >= fromDate && p.StartDate <= toDate);


            var actualFilamentUsage = await baseQuery
                .Where(p => p.FilamentUsageMg.HasValue  && p.FilamentUsageMg.Value > 0)
                .Select(p => p.FilamentUsageMg)
                .SumAsync();

            var estimatedFilamentUsageWhenNoActualWasRecorded = await baseQuery
                .Where(p => (!p.FilamentUsageMg.HasValue || p.FilamentUsageMg.Value == 0) && p.EstimatedFilamentUsageMg.HasValue)
                .Select(p => p.EstimatedFilamentUsageMg)
                .SumAsync();

            int totalFilamentUsage = (actualFilamentUsage ?? 0) + (estimatedFilamentUsageWhenNoActualWasRecorded ?? 0);

            return new SinglePrintStat() { Stat = totalFilamentUsage.ToString()};
        }

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

        [HttpGet("{userId}/total-print-time")]
        [AllowAnonymous]
        public async Task<ActionResult<SinglePrintStat>> GetUsersTotalPrintTimeInSeconds(long userId, [FromQuery] DateTimeOffset fromDate, [FromQuery] DateTimeOffset toDate)
        {
            var printTime = await _context.Prints
                .Where(p => p.CreatedById == userId || p.Printer.UserId == userId)
                .Where(p => p.StartDate >= fromDate && p.StartDate <= toDate)
                .Where(p => p.PrintTimeInSeconds.HasValue || p.EstimatedPrintTimeInSeconds.HasValue)
                .Select(p => p.PrintTimeInSeconds.HasValue ? p.PrintTimeInSeconds : p.EstimatedPrintTimeInSeconds )
                .SumAsync();


            return new SinglePrintStat() { Stat = printTime.ToString() };
        }
    }
}
