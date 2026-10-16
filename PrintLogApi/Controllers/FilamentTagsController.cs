using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PrintLogApi.Models.DTOs.SpoolTags;
using PrintLogApi.SpoolTags;

namespace PrintLogApi.Controllers;

/// <summary>
/// Configuration for reading spool tags in the native app
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class FilamentTagsController(IOptions<TagReaderOptions> readerOptions) : ControllerBase
{
    /// <summary>
    /// How to derive the sector keys for each supported MIFARE Classic tag format.
    /// </summary>
    /// <remarks>
    /// The native app fetches this before reading a tag. The parameters come from server configuration.
    /// An empty `formats` list means no format is configured.
    /// </remarks>
    /// <response code="200">The configured tag formats.</response>
    [HttpGet("reader-config")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ReaderConfigDto> GetReaderConfig()
    {
        Response.Headers.CacheControl = "private, max-age=3600";
        return new ReaderConfigDto
        {
            Formats = readerOptions.Value.Formats
                .Select(f => new ReaderFormatDto
                {
                    Id = f.Id,
                    Scheme = f.Scheme,
                    Params = f.Params is null ? null : new Dictionary<string, string>(f.Params),
                })
                .ToList(),
        };
    }
}
