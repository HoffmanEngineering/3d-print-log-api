using Microsoft.Extensions.Options;
using PrintLogApi.Email;
using PrintLogApi.Email.Templates;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailAssetsTests
{
    private static EmailAssets For(string webBaseUrl) => new(Options.Create(new EmailOptions { WebBaseUrl = webBaseUrl }));

    [Fact]
    public void Urls_LiveUnderTheVersionedEmailFolder()
    {
        var assets = For("https://www.3dprintlog.test");

        Assert.Equal("https://www.3dprintlog.test/assets/email/v1/logo-wordmark.png", assets.Wordmark);
        Assert.Equal("https://www.3dprintlog.test/assets/email/v1/printer-mark.png", assets.PrinterMark);
        Assert.Equal("https://www.3dprintlog.test/assets/email/v1/social-youtube.png", assets.SocialYouTube);
        Assert.Equal("https://www.3dprintlog.test/assets/email/v1/social-github.png", assets.SocialGitHub);
        Assert.Equal("https://www.3dprintlog.test/assets/email/v1/social-blog.png", assets.SocialBlog);
        Assert.Equal("https://www.3dprintlog.test/assets/email/v1/badges/clock-t3.png", assets.Badge("clock-t3.png"));
    }

    [Fact]
    public void Urls_IgnoreATrailingSlashOnTheBaseUrl()
        => Assert.Equal("https://www.3dprintlog.test/assets/email/v1/printer-mark.png", For("https://www.3dprintlog.test/").PrinterMark);
}
