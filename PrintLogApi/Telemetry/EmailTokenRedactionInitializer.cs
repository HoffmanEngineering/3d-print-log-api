using System.Text.RegularExpressions;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace PrintLogApi.Telemetry;

/// <summary>
/// Removes the <c>t</c> query parameter from telemetry about the email endpoints. An unsubscribe
/// token never expires, so one copied out of telemetry would work for as long as the email exists.
/// The page endpoints take their token in a header, which the API's telemetry does not record.
/// </summary>
public sealed partial class EmailTokenRedactionInitializer : ITelemetryInitializer
{
    private const string Redacted = "REDACTED";

    public void Initialize(ITelemetry telemetry)
    {
        switch (telemetry)
        {
            case RequestTelemetry request when IsEmailPath(request.Url?.AbsolutePath):
                request.Url = new Uri(Redact(request.Url!.ToString()));
                request.Name = Redact(request.Name);
                break;
            case DependencyTelemetry dependency when dependency.Data?.Contains("/api/email", StringComparison.OrdinalIgnoreCase) == true:
                dependency.Data = Redact(dependency.Data);
                break;
        }
    }

    private static bool IsEmailPath(string? path)
        => path is not null && (path.Equals("/api/email", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/email/", StringComparison.OrdinalIgnoreCase));

    private static string Redact(string? value) => value is null ? "" : TokenParameter().Replace(value, "$1t=" + Redacted);

    [GeneratedRegex("([?&])t=[^&#]*", RegexOptions.IgnoreCase)]
    private static partial Regex TokenParameter();
}
