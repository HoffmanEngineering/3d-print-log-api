using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Used to retrieve list of prints for a feed-like view. Not currently in use.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class FeedController : ControllerBase
    {
        private readonly TelemetryClient _telemetry;
        private readonly IPrintService _printService;
        private readonly long[]? _allowedUserIds;

        public FeedController(
            TelemetryClient telemetry,
            IPrintService printService,
            IConfiguration config
)
        {
            _telemetry = telemetry;
            _printService = printService;
            _allowedUserIds = config.GetSection("Feed").GetSection("AllowedUserIds").Get<long[]>();
        }

        [HttpGet]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<List<PrintFeedSummaryDto>>> GetFeed(DateTimeOffset? fromDateTime)
        {

            long? currentUserId = User.GetUserId();
            var numberOfRecords = 10;

            if (!currentUserId.HasValue || !this._allowedUserIds.Contains(currentUserId.Value))
            {
                return NotFound();
            }

            var searchDateTime = fromDateTime ?? DateTimeOffset.Now;

            return await this._printService.GetPrintFeedSummary(currentUserId, numberOfRecords, searchDateTime);


            //return await _printService.SearchPrintSummary(pagingRequest, searchText, sortRequest, filterByPrinterIds, filterByStatus, userId, currentUserId);
        }
    }
}
