namespace PrintLogApi.Models.DTOs.Webhook;

/// <summary>A webhook subscription as its owner sees it. Never carries the secret.</summary>
public class WebhookSubscriptionDto
{
    public Guid Id { get; set; }

    public string? Url { get; set; }

    public string? Description { get; set; }

    /// <summary><c>print.started</c>, <c>print.finished</c> and/or <c>print.failed</c>.</summary>
    public List<string>? Events { get; set; }

    public bool Enabled { get; set; }

    /// <summary>Deliveries in a row that failed after their retries.</summary>
    public int ConsecutiveFailures { get; set; }

    public DateTime? LastDeliveryAt { get; set; }

    /// <summary>The HTTP status of the last delivery, or null when no response came back.</summary>
    public int? LastStatusCode { get; set; }

    /// <summary>
    /// <c>success</c>, <c>http_error</c>, <c>timeout</c>, <c>connection_failed</c> or
    /// <c>blocked_address</c>; null before the first delivery.
    /// </summary>
    public string? LastDeliveryResult { get; set; }

    /// <summary>When repeated failures turned it off; turning it back on clears this.</summary>
    public DateTime? AutoDisabledAt { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime UpdatedDate { get; set; }
}

/// <summary>
/// A new subscription, or one whose secret was just rotated: the only responses that carry the
/// signing secret. It cannot be shown again; rotate it to get a new one.
/// </summary>
public class WebhookSubscriptionCreatedDto : WebhookSubscriptionDto
{
    public string? Secret { get; set; }
}

/// <summary>The body of <c>POST /api/Webhooks</c> and <c>PUT /api/Webhooks/{id}</c>.</summary>
public class SaveWebhookSubscriptionDto
{
    /// <summary>An absolute http or https URL on a public address.</summary>
    public string? Url { get; set; }

    /// <summary>A label, at most 100 characters.</summary>
    public string? Description { get; set; }

    /// <summary>The events to send; omitted or empty means all of them.</summary>
    public List<string>? Events { get; set; }

    /// <summary>Omitted means on when creating and unchanged when updating.</summary>
    public bool? Enabled { get; set; }
}

/// <summary>What happened to a test event.</summary>
public class WebhookTestResultDto
{
    /// <summary>True when the receiver answered with a 2xx status.</summary>
    public bool Delivered { get; set; }

    /// <summary>The receiver's HTTP status, or null when no response came back.</summary>
    public int? StatusCode { get; set; }

    /// <summary><c>success</c>, <c>http_error</c>, <c>timeout</c>, <c>connection_failed</c> or <c>blocked_address</c>.</summary>
    public string? Result { get; set; }
}
