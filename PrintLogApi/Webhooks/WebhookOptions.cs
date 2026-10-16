using Microsoft.Extensions.Options;

namespace PrintLogApi.Webhooks;

/// <summary>The <c>Webhooks</c> configuration section (#235).</summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>
    /// Base64 keys, at least 32 bytes each, that encrypt the signing secrets at rest. The first
    /// encrypts; every key decrypts, so a new key goes first and the old one stays until every
    /// secret has been rotated. Empty leaves webhooks unavailable (503) without stopping the app.
    /// </summary>
    public string[] SecretEncryptionKeys { get; set; } = [];

    /// <summary>
    /// Lets a webhook reach loopback and private networks (a Home Assistant on the LAN or in Docker).
    /// Honored only in Development: every other environment ignores it and logs a warning, so
    /// production cannot be opened by a stray setting.
    /// </summary>
    public bool AllowPrivateAddresses { get; set; }

    /// <summary>Timeout for one delivery attempt, connect included.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Waits before each retry; the count is the number of retries.</summary>
    public TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    /// <summary>Failed deliveries in a row (each after its retries) that turn a subscription off.</summary>
    public int MaxConsecutiveFailures { get; set; } = 10;

    /// <summary>The most of a response body read before the connection is dropped.</summary>
    public int MaxResponseBytes { get; set; } = 4096;

    /// <summary>Events waiting for delivery before new ones are dropped.</summary>
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>Events delivered at the same time.</summary>
    public int MaxConcurrentDeliveries { get; set; } = 8;
}

public sealed class WebhookOptionsValidator : IValidateOptions<WebhookOptions>
{
    private const int MinimumKeyBytes = 32;

    public ValidateOptionsResult Validate(string? name, WebhookOptions options)
    {
        var failures = new List<string>();

        // No key is a supported state (webhooks unavailable); a key that is there but unusable is a
        // mistake, and silently ignoring it would encrypt with whichever key happens to be next.
        if (options.SecretEncryptionKeys.Any(k => !TryDecode(k, out var bytes) || bytes.Length < MinimumKeyBytes))
        {
            failures.Add($"{nameof(WebhookOptions.SecretEncryptionKeys)} entries must be base64 and at least {MinimumKeyBytes} bytes.");
        }
        if (options.Timeout <= TimeSpan.Zero) failures.Add($"{nameof(WebhookOptions.Timeout)} must be positive.");
        if (options.RetryDelays.Any(d => d < TimeSpan.Zero)) failures.Add($"{nameof(WebhookOptions.RetryDelays)} must not be negative.");
        if (options.MaxConsecutiveFailures <= 0) failures.Add($"{nameof(WebhookOptions.MaxConsecutiveFailures)} must be positive.");
        if (options.MaxResponseBytes < 0) failures.Add($"{nameof(WebhookOptions.MaxResponseBytes)} must not be negative.");
        if (options.QueueCapacity <= 0) failures.Add($"{nameof(WebhookOptions.QueueCapacity)} must be positive.");
        if (options.MaxConcurrentDeliveries <= 0) failures.Add($"{nameof(WebhookOptions.MaxConcurrentDeliveries)} must be positive.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static bool TryDecode(string? base64, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(base64)) return false;
        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
