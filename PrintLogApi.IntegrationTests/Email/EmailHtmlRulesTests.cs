using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

/// <summary>
/// The rules every campaign email follows whatever its layout: small enough that Gmail won't clip
/// the footer and its unsubscribe link, no markup the major clients drop, every image sized and
/// served from the permanent asset folder, and every important word readable with images blocked.
/// </summary>
public class EmailHtmlRulesTests : IClassFixture<CustomWebApplicationFactory>
{
    private const int MaxBytes = 90 * 1024;
    private const string AssetRoot = "https://www.3dprintlog.test/assets/email/v1/";

    private readonly CustomWebApplicationFactory _factory;

    public EmailHtmlRulesTests(CustomWebApplicationFactory factory) => _factory = factory;

    public static TheoryData<string> Fixtures => new(EmailFixtures.Names);

    private Task<(string Html, string Text)> RenderAsync(string name) => EmailFixtures.Get(name).Render(_factory.Services);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task StaysUnderGmailsClippingLimit(string name)
    {
        var (html, _) = await RenderAsync(name);

        Assert.True(Encoding.UTF8.GetByteCount(html) < MaxBytes, $"{name} is {Encoding.UTF8.GetByteCount(html)} bytes");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task UsesOnlyMarkupMailClientsKeep(string name)
    {
        var (html, _) = await RenderAsync(name);

        Assert.DoesNotContain("<svg", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rel=\"stylesheet\"", html, StringComparison.OrdinalIgnoreCase);
        // Unitless line heights are misread by classic Outlook; zero needs no unit.
        Assert.DoesNotMatch(@"line-height:\s*(?!0\s*[;""])\d+(\.\d+)?\s*(;|"")", html);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task EveryImageIsSizedAndServedFromTheAssetFolder(string name)
    {
        var (html, _) = await RenderAsync(name);

        var images = Regex.Matches(html, "<img [^>]*>").Select(m => m.Value).ToList();
        Assert.NotEmpty(images);
        foreach (var img in images)
        {
            Assert.Contains(" alt=\"", img);
            Assert.Matches(@" width=""\d+""", img);
            Assert.Matches(@" height=""\d+""", img);
            Assert.Contains($"src=\"{AssetRoot}", img);
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task ReadsWithImagesBlocked(string name)
    {
        var fixture = EmailFixtures.Get(name);
        var (html, _) = await RenderAsync(name);
        var visible = EmailLayoutRenderingTests.VisibleText(html);

        foreach (var expected in fixture.MustShow.Append("YouTube").Append("GitHub").Append("Blog"))
        {
            Assert.Contains(expected, visible);
        }
    }
}
