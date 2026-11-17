namespace PrintLogApi.Services.Spoolman;

/// <summary>The <c>Spoolman</c> configuration section (#173).</summary>
public sealed class SpoolmanOptions
{
    public const string SectionName = "Spoolman";

    /// <summary>
    /// The public base of the Spoolman routes, which the ready-to-paste <c>[spoolman]</c> block
    /// puts the credential into: <c>https://api.3dprintlog.com/spoolman</c> in production. Blank
    /// falls back to the request's own origin plus <c>/spoolman</c>, like <c>OpenApi:ServerUrl</c>,
    /// so a local API hands out a block that points at itself.
    /// </summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Where fair-use alerts go. Blank sends nothing, which is what every local environment
    /// should leave it as.
    /// </summary>
    public string? AdminEmailAddress { get; set; }

    /// <summary>
    /// Base64 HMAC key for the stored network hashes. Blank uses a random key per process, which
    /// is safe but forgets the week's networks on every restart, so production should set one.
    /// Changing it forgets them once, the same way.
    /// </summary>
    public string? NetworkHashKey { get; set; }

    /// <summary>Active connections above which the admin is emailed.</summary>
    public int FairUseConnectionThreshold { get; set; } = 20;

    /// <summary>Distinct networks in a rolling week above which the admin is emailed.</summary>
    public int FairUseNetworkThreshold { get; set; } = 5;
}
