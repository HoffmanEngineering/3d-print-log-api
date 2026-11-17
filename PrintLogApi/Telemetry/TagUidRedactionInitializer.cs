using System.Text.RegularExpressions;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace PrintLogApi.Telemetry;

/// <summary>
/// Removes spool tag UIDs from request telemetry. <c>GET /api/Filaments/by-tag/{uid}</c> carries the
/// UID in its path, and the phone app and printlog-bridge call it on every tag read. A UID is a stable
/// identifier for a physical spool, so it stays out of telemetry like any other identifier the user did
/// not choose to share.
/// </summary>
public sealed partial class TagUidRedactionInitializer : ITelemetryInitializer
{
    private const string Redacted = "REDACTED";

    public void Initialize(ITelemetry telemetry)
    {
        if (telemetry is RequestTelemetry request
            && request.Url?.AbsolutePath.Contains("/by-tag/", StringComparison.OrdinalIgnoreCase) == true)
        {
            // AbsoluteUri keeps escapes; ToString would turn %20 into a space.
            request.Url = new Uri(Redact(request.Url.AbsoluteUri));
            request.Name = Redact(request.Name);
        }
    }

    private static string Redact(string? value) => value is null ? "" : ByTagSegment().Replace(value, "$1" + Redacted);

    // Up to the next path, query or fragment delimiter only: a UID may carry spaces as separators.
    [GeneratedRegex("(/by-tag/)[^/?#]+", RegexOptions.IgnoreCase)]
    private static partial Regex ByTagSegment();
}
