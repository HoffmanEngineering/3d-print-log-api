using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using PrintLogApi.Models.DTOs.Seo;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers;

/// <summary>
/// Search and link-preview metadata for the web app's public print, project and profile pages.
/// </summary>
/// <remarks>
/// Read by the Cloudflare Worker that adds meta tags, JSON-LD and a short summary to those pages
/// for crawlers, and by the web app's own meta tags, so both describe a page the same way.
/// Anonymous and rate-limited per client like every other anonymous endpoint (the "api"
/// policy). Responses are <c>Cache-Control: public, max-age=300</c>, so the edge can serve them
/// for five minutes.
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("api")]
public class SeoController(SeoMetadataService seo, IConfiguration configuration) : ControllerBase
{
    /// <summary>How long the edge may serve a response before asking again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Get the search and link-preview metadata for a public print.
    /// </summary>
    /// <remarks>
    /// 404 unless the print is Public: unlisted and private prints, and prints that do not exist,
    /// look the same, even to their creator.
    /// </remarks>
    /// <param name="id">The id of the print.</param>
    /// <response code="200">The print's metadata.</response>
    /// <response code="404">No public print with that id.</response>
    [HttpGet("Prints/{id}/seo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SeoMetadataDto>> GetPrintSeo(long id)
        => Respond(await seo.ForPrintAsync(id, ApiBaseUrl(), HttpContext.RequestAborted));

    /// <summary>
    /// Get the search and link-preview metadata for a public project.
    /// </summary>
    /// <remarks>404 unless the project is Public.</remarks>
    /// <param name="id">The id of the project.</param>
    /// <response code="200">The project's metadata.</response>
    /// <response code="404">No public project with that id.</response>
    [HttpGet("Projects/{id}/seo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SeoMetadataDto>> GetProjectSeo(Guid id)
        => Respond(await seo.ForProjectAsync(id, ApiBaseUrl(), HttpContext.RequestAborted));

    /// <summary>
    /// Get the search and link-preview metadata for a public user profile.
    /// </summary>
    /// <remarks>404 unless the profile is Public.</remarks>
    /// <param name="id">The id of the user.</param>
    /// <response code="200">The profile's metadata.</response>
    /// <response code="404">No public profile with that id.</response>
    [HttpGet("Users/{id}/seo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SeoMetadataDto>> GetUserSeo(long id)
        => Respond(await seo.ForUserAsync(id, HttpContext.RequestAborted));

    private ActionResult<SeoMetadataDto> Respond(SeoMetadataDto? metadata)
    {
        if (metadata is null)
            return NotFound();

        // Public data only, identical for every caller, so a shared cache may keep it whatever
        // credentials the request carried.
        Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = MaxAge };
        return metadata;
    }

    /// <summary>
    /// The absolute base for image URLs: <c>Seo:ApiBaseUrl</c> when set (production sets the
    /// public API host), else this request's own origin.
    /// </summary>
    private string ApiBaseUrl()
    {
        var configured = configuration["Seo:ApiBaseUrl"];
        return string.IsNullOrWhiteSpace(configured)
            ? $"{Request.Scheme}://{Request.Host}"
            : configured.TrimEnd('/');
    }
}
