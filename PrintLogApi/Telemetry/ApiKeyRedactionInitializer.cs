using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace PrintLogApi.Telemetry;

/// <summary>
/// Removes the value of the <c>api_key</c> query parameter from every request URL and name, and
/// from dependency data. <c>ApiKeyMiddleware</c> accepts a personal API key in the query string as
/// well as in <c>X-Api-Key</c>, and request telemetry records the full URL, so without this every
/// keyed call (and every rejected guess) would put a long-lived account credential in Application
/// Insights.
/// </summary>
/// <remarks>
/// ASP.NET Core matches query keys case-insensitively and after percent-decoding, so
/// <c>API_KEY=</c> and <c>api%5Fkey=</c> authenticate too. Each parameter name is therefore
/// decoded and compared the same way, rather than matched as text.
/// </remarks>
public sealed class ApiKeyRedactionInitializer : ITelemetryInitializer
{
    private const string ParameterName = "api_key";
    private const string Redacted = "REDACTED";

    public void Initialize(ITelemetry telemetry)
    {
        switch (telemetry)
        {
            case RequestTelemetry request:
                // AbsoluteUri keeps escapes; ToString would turn %20 into a space.
                if (request.Url is not null && Redact(request.Url.AbsoluteUri) is { } url)
                {
                    request.Url = new Uri(url);
                }
                if (Redact(request.Name) is { } name)
                {
                    request.Name = name;
                }
                break;
            case DependencyTelemetry dependency when Redact(dependency.Data) is { } data:
                dependency.Data = data;
                break;
        }
    }

    /// <summary>The value with every api_key parameter's value replaced, or null when it has none.</summary>
    internal static string? Redact(string? value)
    {
        var queryStart = value?.IndexOf('?') ?? -1;
        if (queryStart < 0)
        {
            return null;
        }

        var fragmentStart = value!.IndexOf('#', queryStart);
        var queryEnd = fragmentStart < 0 ? value.Length : fragmentStart;
        var parameters = value[(queryStart + 1)..queryEnd].Split('&');

        var changed = false;
        for (var i = 0; i < parameters.Length; i++)
        {
            var equals = parameters[i].IndexOf('=');
            var name = equals < 0 ? parameters[i] : parameters[i][..equals];
            if (string.Equals(Decode(name), ParameterName, StringComparison.OrdinalIgnoreCase))
            {
                parameters[i] = name + "=" + Redacted;
                changed = true;
            }
        }

        return changed
            ? string.Concat(value.AsSpan(0, queryStart + 1), string.Join('&', parameters), value.AsSpan(queryEnd))
            : null;
    }

    private static string Decode(string name)
    {
        try
        {
            return Uri.UnescapeDataString(name.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return name;
        }
    }
}
