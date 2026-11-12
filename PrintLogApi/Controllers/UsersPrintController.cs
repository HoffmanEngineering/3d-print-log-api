using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Controllers;

/// <summary>
/// Used to retrieve specific stats about a specific user.
/// </summary>
/// <remarks>
/// The totals follow the profile's visibility, as <c>GET /api/Users/{id}</c> does: anyone may read
/// them for a Public or Unlisted profile, only the user for a Private one, and anyone else gets
/// the same 403 that profile gives them.
/// </remarks>
[Route("api/Users")]
[ApiController]
[Authorize]
[ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client, NoStore = false)]
public class UsersPrintsController(PrintLogContext context, IAuthorizationService authorizationService) : ControllerBase
{
    /// <summary>
    /// A 403 when the caller may not see <paramref name="userId"/>'s profile, by the same
    /// <c>ViewUserProfile</c> policy <c>GET /api/Users/{id}</c> applies; null when the totals may
    /// be answered. An unknown user is answered (with zeros), as before.
    /// </summary>
    private async Task<ActionResult?> RefuseUnlessProfileVisible(long userId)
    {
        if (User.GetUserId() == userId)
        {
            return null;
        }

        var user = await context.Users
            .Where(u => u.Id == userId)
            .AsNoTracking()
            .SingleOrDefaultAsync();

        if (user == null)
        {
            return null;
        }

        var result = await authorizationService.AuthorizeAsync(User, user, "ViewUserProfile");
        return result.Succeeded ? null : Forbid();
    }

    /// <summary>
    /// Get the total amount of filament used by a user between a date range.
    /// </summary>
    /// <param name="userId">The user Id to retrieve</param>
    /// <param name="fromDate">The datetime of the start of the date range (inclusive)</param>
    /// <param name="toDate">The datetime of the end of the date range (inclusive)</param>
    /// <returns></returns>
    /// <response code="403">The user's profile is Private and the caller is not that user.</response>
    [HttpGet("{userId}/total-filament-usage")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SinglePrintStat>> GetUsersTotalFilamentUsage(long userId, [FromQuery] DateTimeOffset fromDate, [FromQuery] DateTimeOffset toDate)
    {
        if (await RefuseUnlessProfileVisible(userId) is { } refused)
        {
            return refused;
        }

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
    /// <response code="403">The user's profile is Private and the caller is not that user.</response>
    [HttpGet("{userId}/print-count")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SinglePrintStat>> GetUsersTotalPrintCount(long userId, [FromQuery] DateTimeOffset fromDate, [FromQuery] DateTimeOffset toDate)
    {
        if (await RefuseUnlessProfileVisible(userId) is { } refused)
        {
            return refused;
        }

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
    /// <response code="403">The user's profile is Private and the caller is not that user.</response>
    [HttpGet("{userId}/total-print-time")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SinglePrintStat>> GetUsersTotalPrintTimeInSeconds(long userId, [FromQuery] DateTimeOffset fromDate, [FromQuery] DateTimeOffset toDate)
    {
        if (await RefuseUnlessProfileVisible(userId) is { } refused)
        {
            return refused;
        }

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
