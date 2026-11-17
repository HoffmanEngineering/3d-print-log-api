namespace PrintLogApi.Services.AgentReleases;

/// <summary>
/// Where the latest release of each connector agent is looked up (configuration section
/// <c>AgentReleases</c>), so a connection can tell its agent that a newer one is out
/// (printlog-bridge#39).
/// </summary>
public sealed class AgentReleaseOptions
{
    public const string SectionName = "AgentReleases";

    /// <summary>
    /// The GitHub repository (<c>owner/name</c>) whose <c>vX.Y.Z</c> tags are the releases of the
    /// agent behind each connection kind. A kind not listed here never gets a latest version.
    /// </summary>
    public Dictionary<string, string> Repositories { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // One entry per printlog-bridge adapter (Connection.BridgeKinds).
        ["moonraker"] = "HoffmanEngineering/printlog-bridge",
        ["octoprint"] = "HoffmanEngineering/printlog-bridge",
        ["prusalink"] = "HoffmanEngineering/printlog-bridge",
        ["bambu"] = "HoffmanEngineering/printlog-bridge",
        ["sdcp"] = "HoffmanEngineering/printlog-bridge",
        ["duet"] = "HoffmanEngineering/printlog-bridge",
        ["flashforge"] = "HoffmanEngineering/printlog-bridge",
        ["anycubic"] = "HoffmanEngineering/printlog-bridge",
    };

    /// <summary>How long a version read from GitHub is served before it is read again.</summary>
    public int CacheMinutes { get; set; } = 6 * 60;

    /// <summary>
    /// How long a failed read is kept before retrying. Agents check in every 5 minutes, so without
    /// it an outage at GitHub would turn every heartbeat into a request there.
    /// </summary>
    public int FailureCacheMinutes { get; set; } = 15;
}
