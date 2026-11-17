namespace PrintLogApi.Models.DTOs.Seo;

/// <summary>
/// Search and link-preview metadata for one public page of the web app: a print, a project or a
/// user profile. The Cloudflare Worker in front of the site writes it into the page's
/// <c>&lt;head&gt;</c> (title, description, canonical link, Open Graph and Twitter tags, JSON-LD)
/// and a short HTML summary; the web app's own meta tags read the same fields.
/// </summary>
/// <remarks>
/// Built only from what the public page itself shows. Additive changes only: the Worker and the
/// web app both read it.
/// </remarks>
public class SeoMetadataDto
{
    /// <summary>
    /// <c>print</c>, <c>project</c> or <c>profile</c>, for choosing the JSON-LD type
    /// (for example <c>CreativeWork</c>, <c>CollectionPage</c>, <c>ProfilePage</c>).
    /// </summary>
    public string? Type { get; set; }

    /// <summary>The page title, with the site suffix: <c>Benchy | 3D Print Log</c>.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Plain text of at most 155 characters: the entity's own notes, description or bio, else a
    /// sentence built from its fields.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>The page's absolute URL on the web app, for <c>rel=canonical</c> and <c>og:url</c>.</summary>
    public string? CanonicalUrl { get; set; }

    /// <summary>The preview image, or null when the page has none.</summary>
    public SeoImageDto? Image { get; set; }

    /// <summary>Who made it. Null when the creator has no display name.</summary>
    public SeoAuthorDto? Author { get; set; }

    /// <summary>When the print or project was created, or the account was. Null when unknown.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>When the print or project was last saved. Null for profiles, and when unknown.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// A few labeled facts for the HTML summary, in display order (for a print: printer,
    /// material, print time, status). Values are display text, not numbers to compute with.
    /// </summary>
    public IList<SeoFactDto>? Facts { get; set; }
}

/// <summary>The preview image. Width and height are null when they are not known.</summary>
public class SeoImageDto
{
    /// <summary>
    /// Absolute URL. For prints and projects, the social-image endpoint on the API host with a
    /// <c>?v=</c> that changes whenever the image does; for profiles, the profile picture.
    /// </summary>
    public string? Url { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Alternative text, for <c>og:image:alt</c>.</summary>
    public string? Alt { get; set; }
}

/// <summary>The creator, for bylines and JSON-LD <c>author</c>.</summary>
public class SeoAuthorDto
{
    public string? Name { get; set; }

    /// <summary>Their profile page, or null when the profile is not public.</summary>
    public string? Url { get; set; }
}

/// <summary>One labeled fact, such as <c>Printer: Prusa MK4</c>.</summary>
public class SeoFactDto
{
    public string? Label { get; set; }

    public string? Value { get; set; }
}
