using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Webhook;

namespace PrintLogApi.Webhooks;

/// <summary>Managing a user's webhook subscriptions (#235). Every call is scoped to the user.</summary>
public interface IWebhookSubscriptionService
{
    Task<List<WebhookSubscriptionDto>> ListAsync(long userId, CancellationToken ct);

    Task<WebhookSubscriptionDto?> GetAsync(long userId, Guid id, CancellationToken ct);

    /// <summary>
    /// Throws <see cref="WebhooksUnavailableException"/>, <see cref="WebhookValidationException"/>
    /// or <see cref="WebhookLimitException"/>.
    /// </summary>
    Task<WebhookSubscriptionCreatedDto> CreateAsync(long userId, SaveWebhookSubscriptionDto dto, CancellationToken ct);

    /// <summary>Null when the subscription is not the user's. Throws as <see cref="CreateAsync"/>.</summary>
    Task<WebhookSubscriptionDto?> UpdateAsync(long userId, Guid id, SaveWebhookSubscriptionDto dto, CancellationToken ct);

    Task<bool> DeleteAsync(long userId, Guid id, CancellationToken ct);

    /// <summary>A new secret, replacing the old one at once. Null when not the user's.</summary>
    Task<WebhookSubscriptionCreatedDto?> RotateSecretAsync(long userId, Guid id, CancellationToken ct);

    /// <summary>
    /// Sends one <c>webhook.test</c> event now: one attempt, no retries, and no change to the
    /// subscription's delivery state. Null when not the user's.
    /// </summary>
    Task<WebhookTestResultDto?> SendTestAsync(long userId, Guid id, CancellationToken ct);
}

public sealed class WebhooksUnavailableException() : Exception("Webhooks are not configured on this server.");

/// <param name="code"><c>invalid_webhook_url</c>, <c>blocked_webhook_address</c> or <c>invalid_webhook</c>.</param>
public sealed class WebhookValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class WebhookLimitException(int limit)
    : Exception($"Your plan includes {limit} webhook{(limit == 1 ? "" : "s")}. Delete one or upgrade to add another.")
{
    public int Limit { get; } = limit;
}

public sealed class WebhookSubscriptionService(
    PrintLogContext context,
    WebhookAddressGuard guard,
    WebhookSecretProtector protector,
    WebhookSender sender,
    TimeProvider clock) : IWebhookSubscriptionService
{
    public const int MaxDescriptionLength = 100;

    public async Task<List<WebhookSubscriptionDto>> ListAsync(long userId, CancellationToken ct)
    {
        var rows = await context.WebhookSubscriptions.AsNoTracking()
            .Where(w => w.UserId == userId)
            .OrderBy(w => w.CreatedDate)
            .ToListAsync(ct);
        return rows.Select(r => Map(r, new WebhookSubscriptionDto())).ToList();
    }

    public async Task<WebhookSubscriptionDto?> GetAsync(long userId, Guid id, CancellationToken ct)
    {
        var row = await context.WebhookSubscriptions.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);
        return row is null ? null : Map(row, new WebhookSubscriptionDto());
    }

    public async Task<WebhookSubscriptionCreatedDto> CreateAsync(long userId, SaveWebhookSubscriptionDto dto, CancellationToken ct)
    {
        RequireConfigured();
        var (checkedUrl, events, description) = await Validate(dto, ct);
        // Validate returns a URL whenever it checked one, which it always does here.
        var url = checkedUrl ?? throw new InvalidOperationException("The URL was not checked.");

        var limit = await IsProAsync(userId, ct) ? SubscriptionLimits.ProMaxWebhooks : SubscriptionLimits.FreeMaxWebhooks;
        if (await context.WebhookSubscriptions.CountAsync(w => w.UserId == userId, ct) >= limit)
        {
            throw new WebhookLimitException(limit);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var secret = WebhookSecretProtector.GenerateSecret();
        var row = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Url = url,
            Description = description,
            Events = events,
            Enabled = dto.Enabled ?? true,
            CreatedDate = now,
            UpdatedDate = now,
        };
        row.EncryptedSecret = protector.Protect(row.Id, secret);
        context.WebhookSubscriptions.Add(row);
        await context.SaveChangesAsync(ct);

        // Counted again now that this row is committed. Two creates that both passed the check
        // above have both inserted by the time the later one counts, so it sees one too many and
        // takes its own row back out. If both count after both inserts, both withdraw and the
        // user retries; the limit is never exceeded either way, and no lock or isolation level is
        // needed (SQLite under test has neither in the form SQL Server does).
        if (await context.WebhookSubscriptions.CountAsync(w => w.UserId == userId, ct) > limit)
        {
            await context.WebhookSubscriptions.Where(w => w.Id == row.Id).ExecuteDeleteAsync(ct);
            throw new WebhookLimitException(limit);
        }

        var created = Map(row, new WebhookSubscriptionCreatedDto());
        created.Secret = secret;
        return created;
    }

    public async Task<WebhookSubscriptionDto?> UpdateAsync(long userId, Guid id, SaveWebhookSubscriptionDto dto, CancellationToken ct)
    {
        RequireConfigured();
        var row = await context.WebhookSubscriptions.FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);
        if (row is null)
        {
            return null;
        }
        // An unchanged URL is not checked against DNS again, so turning a webhook off (or on)
        // never fails on a lookup. Delivery checks every connection anyway.
        var urlUnchanged = string.Equals(dto.Url?.Trim(), row.Url, StringComparison.Ordinal);
        var (url, events, description) = await Validate(dto, ct, checkUrl: !urlUnchanged);

        row.Url = url ?? row.Url;
        row.Events = events;
        row.Description = description;
        if (dto.Enabled is { } enabled)
        {
            row.Enabled = enabled;
            if (enabled)
            {
                // Turning it on is the user saying the receiver is fixed: start counting afresh.
                row.ConsecutiveFailures = 0;
                row.AutoDisabledAt = null;
            }

            // Written even when the tracked values look unchanged. An auto-disable can land between
            // the read above and this save; without forcing the columns, EF would skip them and the
            // user's "on" would silently lose to it.
            var entry = context.Entry(row);
            entry.Property(w => w.Enabled).IsModified = true;
            entry.Property(w => w.ConsecutiveFailures).IsModified = true;
            entry.Property(w => w.AutoDisabledAt).IsModified = true;
        }
        row.UpdatedDate = clock.GetUtcNow().UtcDateTime;
        await context.SaveChangesAsync(ct);
        return Map(row, new WebhookSubscriptionDto());
    }

    public async Task<bool> DeleteAsync(long userId, Guid id, CancellationToken ct) =>
        await context.WebhookSubscriptions.Where(w => w.Id == id && w.UserId == userId).ExecuteDeleteAsync(ct) > 0;

    public async Task<WebhookSubscriptionCreatedDto?> RotateSecretAsync(long userId, Guid id, CancellationToken ct)
    {
        RequireConfigured();
        var row = await context.WebhookSubscriptions.FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);
        if (row is null)
        {
            return null;
        }

        var secret = WebhookSecretProtector.GenerateSecret();
        row.EncryptedSecret = protector.Protect(row.Id, secret);
        row.UpdatedDate = clock.GetUtcNow().UtcDateTime;
        await context.SaveChangesAsync(ct);

        var rotated = Map(row, new WebhookSubscriptionCreatedDto());
        rotated.Secret = secret;
        return rotated;
    }

    public async Task<WebhookTestResultDto?> SendTestAsync(long userId, Guid id, CancellationToken ct)
    {
        RequireConfigured();
        var row = await context.WebhookSubscriptions.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);
        if (row is null)
        {
            return null;
        }

        var eventId = "evt_" + Guid.NewGuid().ToString("N");
        var payload = new WebhookPayload<WebhookTestEventData>(
            WebhookPayload<WebhookTestEventData>.CurrentVersion, eventId, WebhookEventTypes.Test, clock.GetUtcNow(),
            new WebhookTestEventData("This is a test event from 3D Print Log.", row.Id));
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, WebhookJsonContext.Default.WebhookPayloadWebhookTestEventData);

        string secret;
        try
        {
            secret = protector.Unprotect(row.Id, row.EncryptedSecret);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The key that encrypted it is gone. Said plainly, so the user knows to rotate it.
            return new WebhookTestResultDto { Delivered = false, StatusCode = null, Result = WebhookDeliveryResults.SecretUnavailable };
        }

        var result = await sender.SendAsync(new Uri(row.Url), secret,
            new WebhookMessage(WebhookEventTypes.Test, eventId, body), attempt: 1, ct);
        return new WebhookTestResultDto { Delivered = result.Succeeded, StatusCode = result.StatusCode, Result = result.Result };
    }

    private void RequireConfigured()
    {
        if (!protector.IsConfigured)
        {
            throw new WebhooksUnavailableException();
        }
    }

    private async Task<(string? Url, WebhookEvents Events, string? Description)> Validate(
        SaveWebhookSubscriptionDto dto, CancellationToken ct, bool checkUrl = true)
    {
        if (!WebhookEventTypes.TryParse(dto.Events, out var events))
        {
            throw new WebhookValidationException("invalid_webhook",
                $"Events must be {WebhookEventTypes.PrintStarted}, {WebhookEventTypes.PrintFinished} or {WebhookEventTypes.PrintFailed}.");
        }

        var description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        if (description is { Length: > MaxDescriptionLength })
        {
            throw new WebhookValidationException("invalid_webhook", $"The description must be at most {MaxDescriptionLength} characters.");
        }

        if (!checkUrl)
        {
            return (null, events, description);
        }

        var check = await guard.ValidateAsync(dto.Url, ct);
        return check.Status switch
        {
            WebhookUrlStatus.Ok => (check.Uri!.OriginalString, events, description),
            WebhookUrlStatus.Blocked => throw new WebhookValidationException("blocked_webhook_address", check.Error!),
            _ => throw new WebhookValidationException("invalid_webhook_url", check.Error!),
        };
    }

    private Task<bool> IsProAsync(long userId, CancellationToken ct) =>
        context.Subscriptions.AnyAsync(s => s.UserId == userId && s.Status == SubscriptionStatus.Active, ct);

    private static T Map<T>(WebhookSubscription row, T dto) where T : WebhookSubscriptionDto
    {
        dto.Id = row.Id;
        dto.Url = row.Url;
        dto.Description = row.Description;
        dto.Events = WebhookEventTypes.ToNames(row.Events);
        dto.Enabled = row.Enabled;
        dto.ConsecutiveFailures = row.ConsecutiveFailures;
        dto.LastDeliveryAt = row.LastDeliveryAt;
        dto.LastStatusCode = row.LastStatusCode;
        dto.LastDeliveryResult = row.LastDeliveryResult;
        dto.AutoDisabledAt = row.AutoDisabledAt;
        dto.CreatedDate = row.CreatedDate;
        dto.UpdatedDate = row.UpdatedDate;
        return dto;
    }
}
