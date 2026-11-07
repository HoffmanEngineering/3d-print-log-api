namespace PrintLogApi.Telemetry;

/// <summary>
/// Whether this environment sends to Application Insights.
/// </summary>
/// <remarks>
/// <para>
/// <c>appsettings.json</c> carries the production key, so every environment that does not
/// override it sends to production. The test environments were found doing that first, and
/// later a developer machine (requests, and SQL calls to localhost,1434) showed up in the
/// production resource too.
/// </para>
/// <para>
/// Development is off unless <c>ApplicationInsights:EnableInDevelopment</c> is <c>true</c>, for
/// the rare session that is debugging telemetry itself; point the connection string at a
/// non-production resource when you do. The test environments are off whatever the flag says.
/// </para>
/// </remarks>
public static class TelemetryEnvironment
{
    public const string EnableInDevelopmentKey = "ApplicationInsights:EnableInDevelopment";

    public static bool IsDisabled(IHostEnvironment environment, IConfiguration configuration)
    {
        if (environment.IsEnvironment("E2ETesting") || environment.IsEnvironment("IntegrationTesting"))
        {
            return true;
        }

        return environment.IsDevelopment() && !configuration.GetValue(EnableInDevelopmentKey, false);
    }
}
