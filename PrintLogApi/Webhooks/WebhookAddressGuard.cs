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

    /// <summary>
    /// The one answer for a host that is internal <i>or</i> does not resolve, so the endpoint never
    /// tells a caller what this server's DNS can see.
    /// </summary>
    public const string UnreachableHostMessage =
        "The URL's host cannot be used: it does not resolve, or it points to a private or reserved network address.";

    /// <summary>
    /// How long one resolved address gets to accept the connection before the next is tried, so a
    /// host whose first answer is a blackhole still gets a chance on its second.
    /// </summary>
    private static readonly TimeSpan PerAddressConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ports below 1024 a webhook may use: the web ports and Home Assistant's default. Every port
    /// from 1024 up is allowed except the Fetch standard's "bad ports" (services a browser refuses
    /// to talk HTTP to), which keeps the endpoint from being a scanner for SSH, SMTP and the like.
    /// </summary>
    private static readonly HashSet<int> AllowedLowPorts = [80, 443];

    private static readonly HashSet<int> BadPorts =
    [
        1719, 1720, 1723, 2049, 3659, 4045, 4190, 5060, 5061, 6000, 6566,
        6665, 6666, 6667, 6668, 6669, 6679, 6697, 10080,
    ];

    /// <summary>True when a webhook may connect to <paramref name="port"/>.</summary>
    public static bool IsAllowedPort(int port) =>
        AllowedLowPorts.Contains(port) || (port is >= 1024 and <= 65535 && !BadPorts.Contains(port));

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

        if (!IsAllowedPort(uri.Port))
        {
            return WebhookUrlCheck.Blocked("Webhooks can use ports 80, 443 and 1024 and up, except a few reserved ones.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Resolve(uri.IdnHost, ct);
        }
        catch (SocketException)
        {
            return WebhookUrlCheck.Blocked(UnreachableHostMessage);
        }

        // Stricter than connect time, which only skips a bad answer: a host with any internal answer
        // is refused outright, so the user hears about it now rather than through failed deliveries.
        if (addresses.Length == 0 || addresses.Any(a => !WebhookAddressPolicy.IsAllowed(a, AllowPrivateAddresses)))
        {
            return WebhookUrlCheck.Blocked(UnreachableHostMessage);
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
        if (!IsAllowedPort(port))
        {
            throw new WebhookAddressBlockedException("Webhooks cannot use this port.");
        }

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
            using var perAddress = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (allowed.Count > 1)
            {
                perAddress.CancelAfter(PerAddressConnectTimeout);
            }
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), perAddress.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException
                || (ex is OperationCanceledException && !ct.IsCancellationRequested))
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
    /// <param name="maxConnectionsPerServer">
    /// At least <see cref="WebhookOptions.MaxInFlightPerHost"/>: a smaller pool would be a second,
    /// hidden host cap, where hung requests to a shared host hold every connection.
    /// </param>
    public static SocketsHttpHandler Create(WebhookAddressGuard guard, TimeSpan connectTimeout, int maxConnectionsPerServer = 16) => new()
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
        MaxConnectionsPerServer = maxConnectionsPerServer,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        // Receivers are third parties: no traceparent, tracestate, baggage or Request-Id, which
        // would hand them this API's trace and request ids.
        ActivityHeadersPropagator = System.Diagnostics.DistributedContextPropagator.CreateNoOutputPropagator(),
    };
}
