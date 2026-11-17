using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintLogApi.Webhooks;

/// <summary>
/// The body of every webhook (#235). <see cref="Version"/> changes only for a breaking change;
/// fields may be added within a version.
/// </summary>
public sealed record WebhookPayload<TData>(int Version, string Id, string Event, DateTimeOffset CreatedAt, TData Data)
{
    public const int CurrentVersion = 1;
}

/// <summary>The data of a <c>print.*</c> event. Everything in it belongs to the print's owner.</summary>
public sealed record WebhookPrintEventData(
    WebhookPrintData Print,
    WebhookPrinterData Printer,
    IReadOnlyList<WebhookFilamentData> Filament);

/// <param name="Status">The print's status name: <c>Printing</c>, <c>Success</c>, <c>Failed</c>, <c>Cancelled</c> or <c>PartialSuccess</c>.</param>
/// <param name="FinishedAt">The start plus the duration when both are known, else when the finish was recorded; null for <c>print.started</c>.</param>
/// <param name="DurationSeconds">The recorded duration, or null when none is known.</param>
/// <param name="Url">The print's page in the web app.</param>
public sealed record WebhookPrintData(
    long Id,
    string? Title,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int? DurationSeconds,
    int? EstimatedDurationSeconds,
    string? Url);

public sealed record WebhookPrinterData(long Id, string? Name);

/// <param name="Grams">Grams used: the actual amount when recorded, else null.</param>
/// <param name="EstimatedGrams">The slicer's estimate, or null.</param>
public sealed record WebhookFilamentData(
    Guid? FilamentId,
    string? Name,
    string? Brand,
    string? MaterialType,
    string? ColorHex,
    int? Slot,
    double? Grams,
    double? EstimatedGrams);

/// <summary>The data of a <c>webhook.test</c> event.</summary>
public sealed record WebhookTestEventData(string Message, Guid SubscriptionId);

/// <summary>
/// Source-generated metadata for the webhook bodies. They are written here and never by MVC, so
/// the wire format does not move when the API's JSON options do.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(WebhookPayload<WebhookPrintEventData>))]
[JsonSerializable(typeof(WebhookPayload<WebhookTestEventData>))]
public sealed partial class WebhookJsonContext : JsonSerializerContext;
