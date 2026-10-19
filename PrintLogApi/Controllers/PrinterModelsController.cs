using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PrintLogApi.Models.DTOs.PrinterCatalog;
using PrintLogApi.PrinterCatalog;
using PrintLogApi.Services.Sync;

namespace PrintLogApi.Controllers;

/// <summary>
/// The printer catalog: search, popular, lookup by id, slicer-name matching and attribution.
/// Anonymous and cacheable, because every caller sees the same data.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicy)]
public class PrinterModelsController(IPrinterCatalogIndex index, IOptions<PrinterCatalogOptions> options) : ControllerBase
{
    public const string RateLimitPolicy = "catalog";

    private const int DefaultSearchLimit = 20;
    private const int DefaultPopularLimit = 8;
    private const int MaxSearchLimit = 50;
    private const int MaxPopularLimit = 24;
    private const int MaxSearchLength = 100;
    private const int MaxNameLength = 200;

    // The embedded file is large and only its sources are needed here, so read it on first use and
    // keep just those.
    private static readonly Lazy<PrinterCatalogAttributionDto> Attribution = new(() =>
    {
        var file = PrinterCatalogFileLoader.LoadEmbedded();
        return new PrinterCatalogAttributionDto { Attribution = file.Attribution, Sources = file.Sources };
    });

    // The attribution can change without the model data (a new source tag), so its ETag also
    // carries a hash of what it returns.
    private static readonly Lazy<string> AttributionTag = new(() =>
        SyncValidators.Hash(System.Text.Json.JsonSerializer.Serialize(Attribution.Value))[..16]);

    /// <summary>
    /// Search the catalog by make, model or alias. Tolerates spacing, punctuation and a one-letter
    /// typo. An empty search returns the popular list. Retired and hidden models are never offered.
    /// </summary>
    /// <param name="search">What the user typed.</param>
    /// <param name="limit">The most results to return. Defaults to 20 and is capped at 50.</param>
    /// <response code="200">The best matches, best first.</response>
    /// <response code="400">The search is longer than 100 characters (<c>query_too_long</c>) or the limit is not a number.</response>
    /// <response code="304">The caller's copy is current.</response>
    /// <response code="429">Too many requests from this address.</response>
    /// <response code="503">The catalog has not finished loading. Retry shortly.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PrinterModelDto[]), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<PrinterModelDto[]> Search([FromQuery] string? search, [FromQuery] int? limit)
    {
        if (Guard(search, MaxSearchLength, "search") is { } rejected)
        {
            return rejected;
        }

        if (StampValidators())
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(index.Search(search, ClampLimit(limit, DefaultSearchLimit, MaxSearchLimit)).Select(PrinterModelDto.From).ToArray());
    }

    /// <summary>
    /// The models people own most, then the featured models, for a picker that has no query yet.
    /// </summary>
    /// <param name="limit">The most results to return. Defaults to 8 and is capped at 24.</param>
    /// <response code="200">The popular models.</response>
    /// <response code="304">The caller's copy is current.</response>
    /// <response code="429">Too many requests from this address.</response>
    /// <response code="503">The catalog has not finished loading. Retry shortly.</response>
    [HttpGet("popular")]
    [ProducesResponseType(typeof(PrinterModelDto[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<PrinterModelDto[]> GetPopular([FromQuery] int? limit)
    {
        if (Guard(null, 0, "") is { } rejected)
        {
            return rejected;
        }

        if (StampValidators())
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var models = index.Popular(ClampLimit(limit, DefaultPopularLimit, MaxPopularLimit), options.Value.PopularityMinOwners);
        return Ok(models.Select(PrinterModelDto.From).ToArray());
    }

    /// <summary>
    /// Get one model by id. A retired model is still returned, with <c>retired</c> true, so
    /// printers linked to it keep resolving.
    /// </summary>
    /// <param name="id">The catalog id, for example <c>bambu-lab-p1s</c>.</param>
    /// <response code="200">The model.</response>
    /// <response code="304">The caller's copy is current.</response>
    /// <response code="404">No model has this id (<c>printer_model_not_found</c>).</response>
    /// <response code="429">Too many requests from this address.</response>
    /// <response code="503">The catalog has not finished loading. Retry shortly.</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PrinterModelDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<PrinterModelDto> Get(string id)
    {
        if (Guard(null, 0, "") is { } rejected)
        {
            return rejected;
        }

        var model = index.Get(id);
        if (model == null)
        {
            return CodedProblem(
                StatusCodes.Status404NotFound,
                "The printer model was not found in the catalog.",
                "printer_model_not_found");
        }

        if (StampValidators())
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(PrinterModelDto.From(model));
    }

    /// <summary>
    /// Resolve the printer name a slicer reports to one catalog model. No answer is better than a
    /// wrong one, so a name that fits several models, or none, gets 204.
    /// </summary>
    /// <param name="name">The slicer printer name.</param>
    /// <response code="200">The one model the name stands for, and how sure the match is.</response>
    /// <response code="400">The name is longer than 200 characters (<c>query_too_long</c>).</response>
    /// <response code="204">No single model fits the name.</response>
    /// <response code="304">The caller's copy is current.</response>
    /// <response code="429">Too many requests from this address.</response>
    /// <response code="503">The catalog has not finished loading. Retry shortly.</response>
    [HttpGet("match")]
    [ProducesResponseType(typeof(PrinterModelMatchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<PrinterModelMatchDto> Match([FromQuery] string? name)
    {
        if (Guard(name, MaxNameLength, "name") is { } rejected)
        {
            return rejected;
        }

        if (StampValidators())
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var match = string.IsNullOrWhiteSpace(name) ? null : index.Match(name);
        return match == null
            ? NoContent()
            : Ok(new PrinterModelMatchDto { Id = match.Id, Confidence = match.Confidence });
    }

    /// <summary>The upstream projects the catalog facts were read from, and their licenses.</summary>
    /// <response code="200">The attribution text and the sources.</response>
    /// <response code="304">The caller's copy is current.</response>
    /// <response code="429">Too many requests from this address.</response>
    /// <response code="503">The catalog has not finished loading. Retry shortly.</response>
    [HttpGet("attribution")]
    [ProducesResponseType(typeof(PrinterCatalogAttributionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<PrinterCatalogAttributionDto> GetAttribution()
    {
        if (Guard(null, 0, "") is { } rejected)
        {
            return rejected;
        }

        if (StampValidators(AttributionTag.Value))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(Attribution.Value);
    }

    private static int ClampLimit(int? limit, int fallback, int max) =>
        limit is > 0 ? Math.Min(limit.Value, max) : fallback;

    /// <summary>
    /// Rejects what no action should touch: an oversized query, before it is parsed, and any
    /// request made before the first seed. An empty catalog then is "not loaded", not "no
    /// models", and an error is retried by the UI where a cached 200 or 404 would stick for the
    /// session.
    /// </summary>
    private ObjectResult? Guard(string? query, int maxLength, string parameter)
    {
        if (query is { } q && q.Length > maxLength)
        {
            return CodedProblem(
                StatusCodes.Status400BadRequest,
                $"The {parameter} is longer than {maxLength} characters.",
                "query_too_long");
        }

        if (index.IsReady)
        {
            return null;
        }

        var headers = Response.GetTypedHeaders();
        headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        Response.Headers.RetryAfter = "5";
        return CodedProblem(
            StatusCodes.Status503ServiceUnavailable,
            "The printer catalog is still loading.",
            "catalog_not_ready");
    }

    /// <summary>
    /// Stamps the cache headers and says whether the copy the caller holds is current. The ETag is
    /// the catalog version, for every URL: each URL has its own cache entry, and the version
    /// changes whenever anything a read returns could. <paramref name="salt"/> adds data the
    /// version does not cover.
    /// </summary>
    private bool StampValidators(string? salt = null)
    {
        var headers = Response.GetTypedHeaders();
        var etag = new EntityTagHeaderValue($"\"{index.Version}{(salt == null ? "" : "-" + salt)}\"", isWeak: true);
        headers.ETag = etag;
        headers.CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromHours(1) };
        return SyncValidators.IfNoneMatchMatches(Request.GetTypedHeaders(), etag);
    }

    private ObjectResult CodedProblem(int status, string detail, string code)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, detail: detail);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
