using System.Net;
using System.Net.Sockets;

namespace PrintLogApi.Webhooks;

/// <summary>DNS for webhook hosts, behind an interface so tests can choose the answers.</summary>
public interface IWebhookHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class DnsWebhookHostResolver : IWebhookHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

public enum WebhookUrlStatus
{
    Ok,

    /// <summary>Not an absolute http(s) URL we accept, or the host does not resolve.</summary>
    Invalid,

    /// <summary>The host is, or resolves to, an address webhooks may not reach.</summary>
    Blocked,
}

public sealed record WebhookUrlCheck(WebhookUrlStatus Status, Uri? Uri, string? Error)
{
    public static WebhookUrlCheck Invalid(string error) => new(WebhookUrlStatus.Invalid, null, error);
    public static WebhookUrlCheck Blocked(string error) => new(WebhookUrlStatus.Blocked, null, error);
}

/// <summary>Thrown from the connect callback when no address of the host may be reached.</summary>
public sealed class WebhookAddressBlockedException(string message) : Exception(message);

/// <summary>
/// The SSRF guard for outbound webhooks (#235). <see cref="ValidateAsync"/> runs when a subscription
/// is saved, for a clear 400; <see cref="ConnectAsync"/> runs inside the delivery handler on every new
/// connection and is the one that actually protects, since DNS can answer differently by then.
/// </summary>
public sealed class WebhookAddressGuard(IWebhookHostResolver resolver, bool allowPrivateAddresses)
{
    public const int MaxUrlLength = 2048;

    /// <summary>Whether private addresses are let through: <c>AllowPrivateAddresses</c> in Development only.</summary>
    public bool AllowPrivateAddresses { get; } = allowPrivateAddresses;

    public async Task<WebhookUrlCheck> ValidateAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return WebhookUrlCheck.Invalid("A URL is required.");
        }
        url = url.Trim();
        if (url.Length > MaxUrlLength)
        {
            return WebhookUrlCheck.Invalid($"The URL must be at most {MaxUrlLength} characters.");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host))
        {
            return WebhookUrlCheck.Invalid("The URL must be an absolute http or https URL.");
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            // Credentials in the URL would be stored and shown in plain text; Home Assistant needs none.
            return WebhookUrlCheck.Invalid("The URL must not contain a user name or password.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Resolve(uri.IdnHost, ct);
        }
        catch (SocketException)
        {
            return WebhookUrlCheck.Invalid($"The host {uri.IdnHost} could not be resolved.");
        }
        if (addresses.Length == 0)
        {
            return WebhookUrlCheck.Invalid($"The host {uri.IdnHost} could not be resolved.");
        }

        // Stricter than connect time, which only skips a bad answer: a host with any internal answer
        // is refused outright, so the user hears about it now rather than through failed deliveries.
        if (addresses.Any(a => !WebhookAddressPolicy.IsAllowed(a, AllowPrivateAddresses)))
        {
            return WebhookUrlCheck.Blocked("The URL points to a private or reserved network address, which webhooks cannot reach.");
        }

        return new WebhookUrlCheck(WebhookUrlStatus.Ok, uri, null);
    }

    /// <summary>
    /// Resolves <paramref name="host"/> and connects to the first address the policy allows,
    /// returning the connected stream. Throws <see cref="WebhookAddressBlockedException"/> when no
    /// answer is allowed. The address checked is the address connected to, so there is no window
    /// for a second DNS answer.
    /// </summary>
    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var addresses = await Resolve(host, ct);
        var allowed = addresses.Where(a => WebhookAddressPolicy.IsAllowed(a, AllowPrivateAddresses)).ToList();
        if (allowed.Count == 0)
        {
            throw new WebhookAddressBlockedException("The webhook host resolves only to addresses webhooks cannot reach.");
        }

        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last!;
    }

    private async Task<IPAddress[]> Resolve(string host, CancellationToken ct)
    {
        // An address literal (including the forms Uri canonicalizes, such as a decimal 2130706433)
        // is checked as it is, never sent to DNS.
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            return [literal];
        }
        return await resolver.ResolveAsync(host, ct);
    }
}

/// <summary>The primary handler for webhook deliveries (#235).</summary>
public static class WebhookHttpHandlerFactory
{
    public static SocketsHttpHandler Create(WebhookAddressGuard guard, TimeSpan connectTimeout) => new()
    {
        ConnectCallback = (context, ct) => new ValueTask<Stream>(guard.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct)),
        ConnectTimeout = connectTimeout,
        // A redirect would be a second, unvalidated request; a proxy would make the connect callback
        // check the proxy instead of the target.
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        MaxResponseHeadersLength = 16, // KB
        MaxConnectionsPerServer = 4,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
    };
}
