namespace PrintLogApi.Mcp.Docs;

/// <summary>
/// Where the MCP docs surface reads the user documentation from (configuration section
/// <c>Docs</c>). The content is the Markdown twins the UI build publishes, never a copy kept
/// here, so the API never ships stale docs.
/// </summary>
public sealed class DocsOptions
{
    public const string SectionName = "Docs";

    /// <summary>
    /// The site origin. Only this origin is ever fetched: page URLs come from the index, but a
    /// request is always rebuilt from this origin and a validated slug, never taken from the index.
    /// </summary>
    public string BaseUrl { get; set; } = "https://www.3dprintlog.com";

    /// <summary>How long a complete corpus is served before it is fetched again.</summary>
    public int CacheMinutes { get; set; } = 60;

    /// <summary>
    /// How long a failed or partial load is served before retrying. Short, because the usual
    /// cause is a deploy in flight; non-zero, because an anonymous caller must not be able to
    /// turn every request into a fetch against the site.
    /// </summary>
    public int FailureCacheMinutes { get; set; } = 2;

    /// <summary>Largest index (<c>/docs/llms.txt</c>) accepted, in bytes.</summary>
    public int MaxIndexBytes { get; set; } = 256 * 1024;

    /// <summary>Largest single page accepted, in bytes. The release notes are the largest today, at about 110 KB.</summary>
    public int MaxPageBytes { get; set; } = 1024 * 1024;

    /// <summary>Most pages read from the index. Entries past this are ignored.</summary>
    public int MaxPages { get; set; } = 100;
}
