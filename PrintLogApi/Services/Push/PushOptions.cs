namespace PrintLogApi.Services.Push;

public class PushOptions
{
    public const string SectionName = "Push";

    /// <summary>Master switch. When false, no push is ever sent.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Firebase service account JSON. A real secret — Azure App Service configuration,
    /// never the repository.
    /// </summary>
    public string? ServiceAccountJson { get; set; }

    public string ChannelId { get; set; } = "print_status";

    public int TimeToLiveHours { get; set; } = 24;

    public int SendTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Ceiling on how many device tokens one user may hold, and therefore on the batch size
    /// reaching the provider. FirebaseAdmin documents SendEachAsync as taking "up to 500
    /// messages" and issues one HTTP call per message, so an uncapped registration list turns
    /// a single notification into an unbounded fan-out of provider calls.
    /// </summary>
    public int MaxDevicesPerUser { get; set; } = 20;

    /// <summary>
    /// True when every operational value is inside a range the client can actually use.
    /// Credentials are validated separately in <c>Startup</c>; this covers the rest, so an
    /// out-of-range value degrades to the no-op client instead of failing on the first real
    /// send. A zero or negative timeout is the dangerous one: it cancels the send before it
    /// begins, silently dropping every push.
    /// </summary>
    public bool HasValidOperationalValues() =>
        SendTimeoutSeconds > 0
        && TimeToLiveHours > 0
        && MaxDevicesPerUser is > 0 and <= 500
        && !string.IsNullOrWhiteSpace(ChannelId);
}
