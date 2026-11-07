namespace PrintLogApi.Services.SlicerSettings;

/// <summary>How long the slicer uploaders' settings records are kept (<see cref="CuraSettingsRetentionJob"/>).</summary>
public sealed class CuraSettingsRetentionOptions
{
    public const string SectionName = "CuraSettingsRetention";

    /// <summary>Off stops the sweep entirely; nothing else reads these options.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long an uploader record lives. It is read only by the add-print form it opens, so this
    /// is how long a user can leave that form, reload it, or log the same upload again.
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>
    /// Rows per DELETE. A record averages about 90 KB (the base64 snapshot image), so 200 is about
    /// 18 MB of data per statement, small enough not to fill the S0 log allowance.
    /// </summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>Pause between batches, so clearing a backlog leaves the database room for requests.</summary>
    public TimeSpan BatchPause { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan RunInterval { get; set; } = TimeSpan.FromDays(1);
}
