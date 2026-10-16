using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Controllers;

/// <summary>
/// Used to retrieve specific stats about a specific user.
/// </summary>
[Route("api/Users")]
[ApiController]
[Authorize]
[ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client, NoStore = false)]
public class UsersPrintsController(PrintLogContext context) : ControllerBase
{
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
        var baseQuery = context.Prints
            .Where(p => p.CreatedById == userId)
            .Where(p => p.StartDate >= fromDate && p.StartDate <= toDate);

        // The canonical per-print material: every usage row resolved by FilamentProfile.UsageRowMg
        // (actual, else estimate, in whichever measure the row carries), plus the legacy "other"
        // filament scalars, each guarded > 0 so a corrupt negative never subtracts. Rows recorded
        // only as a length or volume counted as nothing here before #229.
        var totalFilamentUsage = await baseQuery.SumAsync(PrintMetrics.MaterialMgExpr);

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
        var printCount = await context.Prints
            .Where(p => p.CreatedById == userId)
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
        // Was: HasValue ? actual : estimated — a stored 0 IS HasValue, so the webhook's coerced
        // zero silently suppressed a perfectly good estimate. The pre-filter is gone with it:
        // the rule already yields 0 for a print with neither, and summing a 0 changes nothing.
        var printTime = await context.Prints
            .Where(p => p.CreatedById == userId)
            .Where(p => p.StartDate >= fromDate && p.StartDate <= toDate)
            .SumAsync(PrintMetrics.DurationSecondsExpr);


        return new SinglePrintStat() { Stat = printTime.ToString() };
    }
}
