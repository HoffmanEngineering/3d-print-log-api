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
    private const int MaxLimit = 50;

    // The embedded file is large and only its sources are needed here, so read it on first use and
    // keep just those.
    private static readonly Lazy<PrinterCatalogAttributionDto> Attribution = new(() =>
    {
        var file = PrinterCatalogFileLoader.LoadEmbedded();
        return new PrinterCatalogAttributionDto { Attribution = file.Attribution, Sources = file.Sources };
    });

    /// <summary>
    /// Search the catalog by make, model or alias. Tolerates spacing, punctuation and a one-letter
    /// typo. An empty search returns the popular list. Retired and hidden models are never offered.
    /// </summary>
    /// <param name="search">What the user typed.</param>
    /// <param name="limit">The most results to return. Defaults to 20 and is capped at 50.</param>
    /// <response code="200">The best matches, best first.</response>
    /// <response code="304">The caller's copy is current.</response>
    /// <response code="429">Too many requests from this address.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PrinterModelDto[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult<PrinterModelDto[]> Search([FromQuery] string? search, [FromQuery] int? limit)
    {
        if (StampValidators())
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(index.Search(search, ClampLimit(limit, DefaultSearchLimit)).Select(PrinterModelDto.From).ToArray());
    }

    /// <summary>
    /// The models people own most, then the featured models, for a picker that has no query yet.
    /// </summary>
    /// <param name="limit">The most results to return. Defaults to 8 and is capped at 50.</param>
    /// <response code="200">The popular models.</response>
    /// <response code="304">The caller's copy is current.</response>
    /// <response code="429">Too many requests from this address.</response>
    [HttpGet("popular")]
    [ProducesResponseType(typeof(PrinterModelDto[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult<PrinterModelDto[]> GetPopular([FromQuery] int? limit)
    {
        if (StampValidators())
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var models = index.Popular(ClampLimit(limit, DefaultPopularLimit), options.Value.PopularityMinOwners);
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
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PrinterModelDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult<PrinterModelDto> Get(string id)
    {
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
    /// <response code="204">No single model fits the name.</response>
    /// <response code="304">The caller's copy is current.</response>
    /// <response code="429">Too many requests from this address.</response>
    [HttpGet("match")]
    [ProducesResponseType(typeof(PrinterModelMatchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult<PrinterModelMatchDto> Match([FromQuery] string? name)
    {
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
    [HttpGet("attribution")]
    [ProducesResponseType(typeof(PrinterCatalogAttributionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult<PrinterCatalogAttributionDto> GetAttribution()
    {
        if (StampValidators())
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(Attribution.Value);
    }

    private static int ClampLimit(int? limit, int fallback) =>
        limit is > 0 ? Math.Min(limit.Value, MaxLimit) : fallback;

    /// <summary>
    /// Stamps the cache headers and says whether the copy the caller holds is current. The ETag is
    /// the catalog version alone, for every URL: each URL has its own cache entry, and the version
    /// changes whenever anything a read returns could. Until the first seed the index is empty
    /// because nothing has loaded, so that answer must not be cached by a browser or a CDN.
    /// </summary>
    private bool StampValidators()
    {
        var headers = Response.GetTypedHeaders();
        if (!index.IsReady)
        {
            headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return false;
        }

        var etag = new EntityTagHeaderValue($"\"{index.Version}\"", isWeak: true);
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
