using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email;
using PrintLogApi.Email.Tokens;
using PrintLogApi.Models.DTOs.Email;

namespace PrintLogApi.Controllers;

/// <summary>
/// The links in an email. Anonymous by design: the signed token is the credential, and each kind
/// can do only its own job (spec §6.7, §6.8).
/// </summary>
[Route("api/email")]
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(EmailRateLimiting.TokenPolicy)]
[ApiExplorerSettings(IgnoreApi = true)]
public class EmailController(
    IEmailTokenService tokens,
    IEmailPreferenceService preferences,
    PrintLogContext context,
    TelemetryClient telemetry) : ControllerBase
{
    private static readonly int[] UnsubscribableCategories =
        [EmailSettingTypes.All, EmailSettingTypes.Onboarding, EmailSettingTypes.MonthlyRecap, EmailSettingTypes.PrinterSilent];

    /// <summary>
    /// RFC 8058 one-click unsubscribe, POSTed by mail clients from the List-Unsubscribe header. The
    /// form must carry List-Unsubscribe=One-Click (once), so link scanners that prefetch the URL
    /// (GET, or POST with no body) never unsubscribe anyone. Other fields are tolerated: the RFC
    /// requires that key, not that it be alone, and refusing a real unsubscribe costs more than
    /// accepting a request that explicitly asked for one.
    /// </summary>
    [HttpPost("unsubscribe")]
    public async Task<IActionResult> OneClick([FromQuery] string? t, CancellationToken ct)
    {
        if (!Request.HasFormContentType)
        {
            return BadRequest();
        }

        var form = await Request.ReadFormAsync(ct);
        if (form["List-Unsubscribe"] is not { Count: 1 } value || value[0] != "One-Click")
        {
            return BadRequest();
        }

        if (!TryReadUnsubscribe(t, out var payload))
        {
            return BadRequest();
        }

        await UnsubscribeAsync(payload, ct);
        telemetry.TrackEvent("EmailUnsubscribe_OneClick", new Dictionary<string, string> { ["category"] = payload.Category.ToString() });
        return Ok();
    }

    /// <summary>The button on the page the footer's "Unsubscribe" link opens.</summary>
    [HttpPost("unsubscribe/confirm")]
    public async Task<ActionResult<UnsubscribeConfirmedDto>> Confirm(
        [FromHeader(Name = EmailTokenHeader.Name)] string? token, CancellationToken ct)
    {
        if (!TryReadUnsubscribe(token, out var payload))
        {
            return BadRequest();
        }

        await UnsubscribeAsync(payload, ct);
        telemetry.TrackEvent("EmailUnsubscribe_Confirmed", new Dictionary<string, string> { ["category"] = payload.Category.ToString() });
        return new UnsubscribeConfirmedDto(payload.Category);
    }

    [HttpGet("preferences")]
    public async Task<ActionResult<EmailPreferencesDto>> GetPreferences(
        [FromHeader(Name = EmailTokenHeader.Name)] string? token, CancellationToken ct)
    {
        if (!tokens.TryValidate(token, EmailTokenKind.Manage, out var payload))
        {
            return BadRequest();
        }

        var dto = await LoadAsync(payload.UserId, ct);
        return dto is null ? BadRequest() : dto;
    }

    [HttpPut("preferences")]
    public async Task<ActionResult<EmailPreferencesDto>> UpdatePreferences(
        [FromHeader(Name = EmailTokenHeader.Name)] string? token,
        UpdateEmailPreferencesRequest request,
        CancellationToken ct)
    {
        if (!tokens.TryValidate(token, EmailTokenKind.Manage, out var payload)
            || !await context.Users.AnyAsync(u => u.Id == payload.UserId, ct))
        {
            return BadRequest();
        }

        await preferences.SetManyAsync(payload.UserId, new Dictionary<int, bool>
        {
            [EmailSettingTypes.All] = request.All,
            [EmailSettingTypes.Onboarding] = request.Onboarding,
            [EmailSettingTypes.MonthlyRecap] = request.MonthlyRecap,
            [EmailSettingTypes.PrinterSilent] = request.PrinterSilent,
        }, ct);
        telemetry.TrackEvent("EmailPreferences_Updated", new Dictionary<string, string> { ["source"] = "token" });

        return (await LoadAsync(payload.UserId, ct))!;
    }

    private bool TryReadUnsubscribe(string? token, out EmailTokenPayload payload)
        => tokens.TryValidate(token, EmailTokenKind.Unsubscribe, out payload)
            && UnsubscribableCategories.Contains(payload.Category);

    private async Task UnsubscribeAsync(EmailTokenPayload payload, CancellationToken ct)
    {
        // A deleted account has nothing left to unsubscribe; the request still succeeds, as
        // RFC 8058 clients expect, rather than tripping the settings foreign key.
        if (await context.Users.AnyAsync(u => u.Id == payload.UserId, ct))
        {
            await preferences.SetAsync(payload.UserId, payload.Category, false, ct);
        }
    }

    private async Task<EmailPreferencesDto?> LoadAsync(long userId, CancellationToken ct)
    {
        var email = await context.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.Email })
            .SingleOrDefaultAsync(ct);
        if (email is null)
        {
            return null;
        }

        var prefs = await preferences.GetAsync(userId, ct);
        return new EmailPreferencesDto(
            EmailMasking.Mask(email.Email ?? ""), prefs.All, prefs.Onboarding, prefs.MonthlyRecap, prefs.PrinterSilent);
    }
}
