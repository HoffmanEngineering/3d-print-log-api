using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;

namespace PrintLogApi.Email.Events;

/// <summary>
/// Applies one SES event (published by the configuration set to SNS) to the database, in one
/// transaction (spec §6.10). Idempotent, so an SNS retry after a failure is safe. Throws on any
/// failure so the webhook answers 500 and SNS retries.
/// </summary>
public sealed class SesEventProcessor(
    PrintLogContext db,
    IEmailSuppressionService suppressions,
    IEmailPreferenceService preferences,
    IEmailAddressHasher hasher,
    TelemetryClient telemetry)
{
    public async Task ProcessAsync(string sesEventJson, CancellationToken ct)
    {
        var root = JsonNode.Parse(sesEventJson)?.AsObject()
            ?? throw new FormatException("SES event body is not a JSON object.");

        // Configuration-set events say eventType; identity notifications say notificationType.
        var type = (string?)root["eventType"] ?? (string?)root["notificationType"];
        var mail = root["mail"] as JsonObject;
        var messageId = (string?)mail?["messageId"];

        if (type is not ("Bounce" or "Complaint" or "Delivery" or "Reject"))
        {
            telemetry.TrackEvent("Email_EventIgnored", new Dictionary<string, string> { ["type"] = type ?? "(none)" });
            return;
        }

        // The API's SQL Server connection retries transient failures, and a retrying execution
        // strategy refuses user-started transactions unless the whole unit runs inside it.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            switch (type)
            {
                case "Bounce":
                    await BounceAsync(root["bounce"] as JsonObject, ct);
                    break;
                case "Complaint":
                    await ComplaintAsync(root["complaint"] as JsonObject, mail, messageId, ct);
                    break;
                case "Delivery":
                    await DeliveryAsync(root["delivery"] as JsonObject, mail, messageId, ct);
                    break;
                case "Reject":
                    await RejectAsync(root["reject"] as JsonObject, messageId, ct);
                    break;
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }

    private async Task BounceAsync(JsonObject? bounce, CancellationToken ct)
    {
        var bounceType = (string?)bounce?["bounceType"] ?? "Undetermined";
        telemetry.TrackEvent("Email_Bounced", new Dictionary<string, string> { ["type"] = bounceType });

        // Transient (mailbox full, greylisting) and Undetermined bounces are SES's to retry;
        // only a permanent bounce means the address will never work.
        if (bounceType != "Permanent")
        {
            return;
        }

        var detail = $"{bounceType}/{(string?)bounce?["bounceSubType"]} {(string?)bounce?["feedbackId"]}".Trim();
        foreach (var address in Addresses(bounce?["bouncedRecipients"]))
        {
            await suppressions.SuppressAsync(address, isHash: false, EmailSuppressionReason.Bounce, detail, ct);
            EmailMetrics.Bounced.Inc();
        }
    }

    private async Task ComplaintAsync(JsonObject? complaint, JsonObject? mail, string? messageId, CancellationToken ct)
    {
        telemetry.TrackEvent("Email_Complained");
        EmailMetrics.Complained.Inc();
        var detail = (string?)complaint?["feedbackId"];

        // The message id is only on the row once the dispatcher has saved it after the send; the
        // outbox_id tag finds the row even when that save never happened.
        var row = messageId is null
            ? null
            : await db.EmailOutbox.AsNoTracking()
                .Where(o => o.ProviderMessageId == messageId)
                .Select(o => new { o.UserId, o.SentTo })
                .FirstOrDefaultAsync(ct);
        if (row is null && OutboxId(mail) is { } outboxId)
        {
            row = await db.EmailOutbox.AsNoTracking()
                .Where(o => o.Id == outboxId)
                .Select(o => new { o.UserId, o.SentTo })
                .FirstOrDefaultAsync(ct);
        }

        var addresses = Addresses(complaint?["complainedRecipients"]).ToList();
        foreach (var address in addresses)
        {
            await suppressions.SuppressAsync(address, isHash: false, EmailSuppressionReason.Complaint, detail, ct);
        }

        // Some feedback loops redact the recipient. The outbox row recorded a hash of the address
        // it went to, and SES's own copy of the destination is in the event either way.
        if (addresses.Count == 0)
        {
            if (row?.SentTo is { } sentTo)
            {
                await suppressions.SuppressAsync(sentTo, isHash: true, EmailSuppressionReason.Complaint, detail, ct);
            }
            else if (First(mail?["destination"]) is { } destination)
            {
                await suppressions.SuppressAsync(destination, isHash: false, EmailSuppressionReason.Complaint, detail, ct);
            }
        }

        // A complaint is an objection to all of it, not to one campaign.
        var userIds = new HashSet<long>();
        if (row is not null)
        {
            userIds.Add(row.UserId);
        }

        foreach (var address in addresses)
        {
            var normalized = EmailAddressHasher.Normalize(address);
            var matches = await db.Users.AsNoTracking()
                .Where(u => u.Email != null && u.Email.ToLower() == normalized)
                .Select(u => u.Id)
                .ToListAsync(ct);
            userIds.UnionWith(matches);
        }

        foreach (var userId in userIds)
        {
            await preferences.SetAsync(userId, EmailSettingTypes.All, false, ct);
        }
    }

    private async Task DeliveryAsync(JsonObject? delivery, JsonObject? mail, string? messageId, CancellationToken ct)
    {
        telemetry.TrackEvent("Email_Delivered");

        // An ambiguous failure (timeout, abandoned claim) may in fact have gone out, and a row
        // still Sending is one whose post-send save has not landed (or never will, if it failed,
        // and the reaper would later call it ambiguous). The outbox_id tag lets SES's own record
        // settle both.
        if (OutboxId(mail) is not { } outboxId)
        {
            return;
        }

        var row = await db.EmailOutbox.SingleOrDefaultAsync(o => o.Id == outboxId, ct);
        var ambiguous = row is { Status: EmailOutboxStatus.Failed }
            && row.LastError?.StartsWith("ambiguous:", StringComparison.Ordinal) == true;
        if (row is null || !(ambiguous || row.Status == EmailOutboxStatus.Sending))
        {
            return;
        }

        var deliveredAt = DateTimeOffset.TryParse((string?)delivery?["timestamp"] ?? (string?)mail?["timestamp"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : (DateTimeOffset?)null;

        row.Status = EmailOutboxStatus.Sent;
        row.SentAt = deliveredAt ?? row.ClaimedAt ?? row.CreatedAt;
        row.ProviderMessageId ??= messageId;
        row.LastError = null;
        if (row.SentTo is null && First(mail?["destination"]) is { } to)
        {
            row.SentTo = hasher.Hash(to);
        }

        telemetry.TrackEvent("Email_Reconciled", new Dictionary<string, string> { ["campaign"] = row.Campaign });
    }

    private async Task RejectAsync(JsonObject? reject, string? messageId, CancellationToken ct)
    {
        if (messageId is null)
        {
            return;
        }

        var row = await db.EmailOutbox.FirstOrDefaultAsync(o => o.ProviderMessageId == messageId, ct);
        if (row is not null)
        {
            row.LastError = $"rejected: {(string?)reject?["reason"]}";
        }
    }

    private static long? OutboxId(JsonObject? mail)
        => long.TryParse(First(mail?["tags"]?["outbox_id"]), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>The first string in a JSON array, or null when it is missing or empty (SES sends both).</summary>
    private static string? First(JsonNode? node) => node is JsonArray { Count: > 0 } array ? (string?)array[0] : null;

    private static IEnumerable<string> Addresses(JsonNode? recipients)
        => (recipients as JsonArray ?? [])
            .Select(r => (string?)r?["emailAddress"])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!);
}
