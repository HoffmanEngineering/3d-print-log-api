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
    /// Rows per DELETE. A record averages about 90 KB (the base64 snapshot image), so 50 is about
    /// 4.5 MB per statement. On the S0 database 200 rows took 14 to 28 s, against SqlClient's 30 s
    /// command timeout, and timed out within the first run's 20 batches (November 2026); 50 takes
    /// about a quarter of that.
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Pause between batches, so clearing a backlog leaves the database room for requests.</summary>
    public TimeSpan BatchPause { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest one run keeps deleting. A run that reaches it stops between batches and leaves
    /// the rest to the next run, so a backlog is cleared in slices instead of holding the
    /// database's log throughput for most of a day.
    /// </summary>
    public TimeSpan MaxRunDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Time between runs once nothing expired is left.</summary>
    public TimeSpan RunInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Time to the next run when one ended with expired records left: it reached
    /// <see cref="MaxRunDuration"/>, or a batch failed (a timeout, a dropped connection).
    /// </summary>
    public TimeSpan BacklogRetryInterval { get; set; } = TimeSpan.FromHours(1);
}
