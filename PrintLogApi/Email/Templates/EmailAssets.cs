using Microsoft.Extensions.Options;

namespace PrintLogApi.Email.Templates;

/// <summary>
/// URLs of the images emails show. The UI serves them from <c>src/assets/email/v1/</c> and never
/// renames or deletes one, because a sent email points at its images forever; see the UI's
/// AGENTS.md, "Email assets". New art goes in a new version folder, never over v1.
/// </summary>
public sealed class EmailAssets(IOptions<EmailOptions> options)
{
    private readonly string _root = options.Value.WebBaseUrl.TrimEnd('/') + "/assets/email/v1/";

    /// <summary>White wordmark on a baked-in indigo background, 360x161 for 180x80.</summary>
    public string Wordmark => _root + "logo-wordmark.png";

    /// <summary>The printer mark, 56x56 for 28x28.</summary>
    public string PrinterMark => _root + "printer-mark.png";

    public string SocialYouTube => _root + "social-youtube.png";

    public string SocialGitHub => _root + "social-github.png";

    public string SocialBlog => _root + "social-blog.png";

    /// <summary>A badge image, by the file name <see cref="Campaigns.BadgeImage"/> builds.</summary>
    public string Badge(string fileName) => _root + "badges/" + fileName;
}
