using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Webhooks;

/// <summary>One serialized event: the same bytes and id for every attempt and subscription.</summary>
public sealed record WebhookMessage(string EventType, string EventId, byte[] Body);

/// <summary>The outcome names stored in <c>LastDeliveryResult</c> and returned by the test endpoint.</summary>
public static class WebhookDeliveryResults
{
    public const string Success = "success";
    public const string HttpError = "http_error";
    public const string Timeout = "timeout";
    public const string ConnectionFailed = "connection_failed";
    public const string BlockedAddress = "blocked_address";
}

public sealed record WebhookAttemptResult(string Result, int? StatusCode, bool Retryable)
{
    public bool Succeeded => Result == WebhookDeliveryResults.Success;
}

/// <summary>
/// Sends one attempt of one webhook (#235): signed, with a short timeout, through the guarded
/// handler (no redirects, connect-time address check), reading at most a few KB of the response.
/// Logs the host and status only: the URL path can be a secret, and bodies are never logged.
/// </summary>
public sealed class WebhookSender(
    IHttpClientFactory httpClientFactory,
    IOptions<WebhookOptions> options,
    TimeProvider clock,
    ILogger<WebhookSender> logger)
{
    public const string HttpClientName = "Webhooks";
    public const string EventHeader = "X-PrintLog-Event";
    public const string EventIdHeader = "X-PrintLog-Event-Id";
    public const string AttemptHeader = "X-PrintLog-Delivery-Attempt";

    private readonly WebhookOptions _options = options.Value;

    public async Task<WebhookAttemptResult> SendAsync(Uri url, string secret, WebhookMessage message, int attempt, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);

        var result = await Attempt(url, secret, message, attempt, timeout.Token, ct);
        logger.LogInformation(
            "Webhook {EventId} ({EventType}) attempt {Attempt} to {Host}: {Result} {StatusCode}",
            message.EventId, message.EventType, attempt, url.Host, result.Result, result.StatusCode);
        return result;
    }

    private async Task<WebhookAttemptResult> Attempt(Uri url, string secret, WebhookMessage message, int attempt, CancellationToken token, CancellationToken outer)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new ByteArrayContent(message.Body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.Add(EventHeader, message.EventType);
            request.Headers.Add(EventIdHeader, message.EventId);
            request.Headers.Add(AttemptHeader, attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add(WebhookSignature.HeaderName,
                WebhookSignature.Create(secret, clock.GetUtcNow().ToUnixTimeSeconds(), message.Body));

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            await DrainCapped(response, token);

            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return new WebhookAttemptResult(WebhookDeliveryResults.Success, status, Retryable: false);
            }
            var retryable = status >= 500
                || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
            return new WebhookAttemptResult(WebhookDeliveryResults.HttpError, status, retryable);
        }
        catch (Exception ex) when (IsBlocked(ex))
        {
            return new WebhookAttemptResult(WebhookDeliveryResults.BlockedAddress, null, Retryable: false);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            return new WebhookAttemptResult(WebhookDeliveryResults.Timeout, null, Retryable: true);
        }
        catch (HttpRequestException)
        {
            return new WebhookAttemptResult(WebhookDeliveryResults.ConnectionFailed, null, Retryable: true);
        }
    }

    /// <summary>
    /// Reads and discards at most <see cref="WebhookOptions.MaxResponseBytes"/> of the body. Nothing
    /// in it is used; reading a little lets a small response free its connection, and the cap stops
    /// a hostile receiver from streaming forever.
    /// </summary>
    private async Task DrainCapped(HttpResponseMessage response, CancellationToken token)
    {
        if (_options.MaxResponseBytes <= 0)
        {
            return;
        }
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(_options.MaxResponseBytes, 4096));
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var remaining = _options.MaxResponseBytes;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), token);
                if (read == 0) break;
                remaining -= read;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // The status already arrived; a body cut short changes nothing.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsBlocked(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is WebhookAddressBlockedException) return true;
        }
        return false;
    }
}
