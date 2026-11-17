namespace PrintLogApi.Services;

/// <summary>
/// Chooses where Stripe sends a customer back to after checkout or the billing portal. Never from
/// the request alone: a caller can send any <c>Origin</c> or <c>Host</c>, and a genuine,
/// 3D Print Log-branded Stripe page that ends on an attacker's site is a phishing aid.
/// </summary>
public static class StripeReturnUrls
{
    /// <summary>
    /// The base the return URLs are built on, without a trailing slash: the request's
    /// <paramref name="requestOrigin"/> when it is exactly one of <paramref name="allowedOrigins"/>,
    /// otherwise <paramref name="webBaseUrl"/>. Null when <paramref name="webBaseUrl"/> is not an
    /// absolute http(s) URL, because then there is no safe default for anyone else.
    /// </summary>
    public static string? ResolveBase(string? requestOrigin, string? webBaseUrl, IEnumerable<string>? allowedOrigins)
    {
        if (string.IsNullOrWhiteSpace(webBaseUrl)
            || !Uri.TryCreate(webBaseUrl.Trim(), UriKind.Absolute, out var webBase)
            || (webBase.Scheme != Uri.UriSchemeHttps && webBase.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var configured = webBase.GetLeftPart(UriPartial.Path).TrimEnd('/');

        if (TryNormalizeOrigin(requestOrigin, out var origin) && allowedOrigins is not null)
        {
            foreach (var allowed in allowedOrigins)
            {
                if (TryNormalizeOrigin(allowed, out var normalized) && string.Equals(normalized, origin, StringComparison.Ordinal))
                {
                    return normalized;
                }
            }
        }

        // The configured origin itself, any other origin and no origin all return to the
        // configured base. The request's Host is never consulted.
        return configured;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is an http or https origin (scheme, host, optional port,
    /// optionally a trailing slash and nothing else), and its normalized form: lowercase host, no
    /// default port, no trailing slash.
    /// </summary>
    public static bool TryNormalizeOrigin(string? value, out string origin)
    {
        origin = "";
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.UserInfo.Length > 0
            || uri.AbsolutePath != "/"
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
