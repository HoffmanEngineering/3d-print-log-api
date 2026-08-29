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
}
