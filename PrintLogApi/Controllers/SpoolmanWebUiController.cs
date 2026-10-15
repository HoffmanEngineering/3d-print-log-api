using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PrintLogApi.Email;

namespace PrintLogApi.Controllers;

/// <summary>
/// Spoolman's web UI paths, which Fluidd builds off the <c>[spoolman]</c> server URL: "Manage
/// spools" opens the base, and the AFC panel opens <c>{base}/spool/show/{id}</c>. 3D Print Log has
/// no Spoolman UI, so both redirect to the web app's materials.
///
/// <para>Anonymous, because a browser following the link may carry no credential, or the block's
/// revoked one. The redirect names nothing but a spool number the caller already had; the UI looks
/// it up as the signed-in user.</para>
/// </summary>
[Route("spoolman")]
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[AllowAnonymous]
public class SpoolmanWebUiController(IOptions<EmailOptions> emailOptions) : ControllerBase
{
    [HttpGet("")]
    public IActionResult Base() => Redirect($"{Web}/materials");

    [HttpGet("spool/show/{id:int}")]
    public IActionResult SpoolShow(int id) => Redirect($"{Web}/materials/spool/{id}");

    private string Web => emailOptions.Value.WebBaseUrl.TrimEnd('/');
}
