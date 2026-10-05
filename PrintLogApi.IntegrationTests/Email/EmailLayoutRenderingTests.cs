using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Templates;
using PrintLogApi.IntegrationTests.Email.Golden;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailLayoutRenderingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EmailLayoutRenderingTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static readonly EmailFooterModel Footer = new(
        ReasonLine: "You're receiving this because you have a 3D Print Log account and monthly recaps are on.",
        ManageUrl: "https://www.3dprintlog.test/email-preferences#m=MANAGE",
        UnsubscribeUrl: "https://www.3dprintlog.test/email-preferences#u=UNSUB",
        PostalAddress: "PO Box 0, Testville, USA",
        HomeUrl: "https://www.3dprintlog.test/");

    private const string AskCopy = "3D Print Log is built by one person. If it's useful to you, going Pro keeps it growing.";
    private const string ThanksCopy = "Thanks for supporting 3D Print Log as a Pro member.";

    private Task<string> RenderAsync(EmailFooterModel? footer = null)
    {
        var renderer = _factory.Services.GetRequiredService<IEmailTemplateRenderer>();
        RenderFragment child = builder =>
        {
            builder.OpenElement(0, "p");
            builder.AddContent(1, "Child content & more");
            builder.CloseElement();
            builder.OpenComponent<EmailButton>(2);
            builder.AddComponentParameter(3, nameof(EmailButton.Href), "https://www.3dprintlog.test/prints?a=1&b=2");
            builder.AddComponentParameter(4, nameof(EmailButton.Text), "Open your prints");
            builder.CloseComponent();
        };

        return renderer.RenderAsync<EmailLayout>(new Dictionary<string, object?>
        {
            [nameof(EmailLayout.Title)] = "Your November in prints",
            [nameof(EmailLayout.Preheader)] = "12 prints, 1.2 kg of filament",
            [nameof(EmailLayout.Kicker)] = "Monthly recap",
            [nameof(EmailLayout.Headline)] = "Your November in prints",
            [nameof(EmailLayout.Footer)] = footer ?? Footer,
            [nameof(EmailLayout.ChildContent)] = child,
        });
    }

    /// <summary>What a reader sees: tags stripped, entities decoded, whitespace collapsed.</summary>
    internal static string VisibleText(string html)
        => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ");

    [Fact]
    public async Task Layout_MatchesApproved()
        => GoldenFile.AssertMatches(await RenderAsync(), "EmailLayout.approved.html");

    // Pinned separately so a careless golden update cannot drop what the law or the mail
    // clients require.
    [Fact]
    public async Task Layout_CarriesRequiredParts()
    {
        var html = await RenderAsync();

        Assert.Contains("<meta name=\"color-scheme\" content=\"light dark\"", html);
        Assert.Contains("PO Box 0, Testville, USA", html);
        Assert.Contains("https://www.3dprintlog.test/email-preferences#m=MANAGE", html);
        Assert.Contains("https://www.3dprintlog.test/email-preferences#u=UNSUB", html);
        Assert.Contains("max-width:600px", html);
        Assert.Contains("12 prints, 1.2 kg of filament", html);
        Assert.Contains("Child content &amp; more", html);
        Assert.Contains("href=\"https://www.3dprintlog.test/prints?a=1&amp;b=2\"", html);
    }

    [Fact]
    public async Task Layout_RendersTheBrandedHeadlineBand()
    {
        var html = await RenderAsync();

        Assert.Contains("src=\"https://www.3dprintlog.test/assets/email/v1/logo-wordmark.png\"", html);
        Assert.Contains("alt=\"3D Print Log\" width=\"180\" height=\"80\"", html);
        Assert.Contains("bgcolor=\"#3f51b5\"", html);
        Assert.Contains(">Monthly recap<", html);
        Assert.Contains(">Your November in prints<", html);
    }

    [Fact]
    public async Task Layout_StylesDarkModeAndAvoidsUnitlessLineHeights()
    {
        var html = await RenderAsync();

        Assert.Contains("@media (prefers-color-scheme: dark)", html);
        Assert.DoesNotMatch(@"line-height:\d+(\.\d+)?(;|"")", html);
    }

    [Fact]
    public async Task Footer_LinksYouTubeGitHubAndBlogAsText()
    {
        var html = await RenderAsync();

        Assert.Contains("href=\"https://www.youtube.com/@hoffmanengineering\"", html);
        Assert.Contains("href=\"https://github.com/HoffmanEngineering/3d-print-log-ui\"", html);
        Assert.Contains("href=\"https://hoffman.engineering/\"", html);
        Assert.Matches(@">\s*YouTube\s*</a>", html);
        Assert.Matches(@">\s*GitHub\s*</a>", html);
        Assert.Matches(@">\s*Blog\s*</a>", html);
    }

    [Fact]
    public async Task Footer_AsksFreeUsersToGoPro()
    {
        var html = await RenderAsync(Footer with
        {
            Supporter = SupporterLine.Ask,
            SubscriptionUrl = "https://www.3dprintlog.test/subscription?utm_source=email",
        });

        Assert.Contains(AskCopy, VisibleText(html));
        Assert.Contains("href=\"https://www.3dprintlog.test/subscription?utm_source=email\"", html);
        Assert.DoesNotContain(ThanksCopy, html);
    }

    [Fact]
    public async Task Footer_ThanksProMembers()
    {
        var html = await RenderAsync(Footer with { Supporter = SupporterLine.Thanks });

        Assert.Contains(ThanksCopy, VisibleText(html));
        Assert.DoesNotContain("going Pro", html);
    }

    [Fact]
    public async Task Footer_OmitsTheSupporterLineByDefault()
    {
        var html = await RenderAsync();

        Assert.DoesNotContain("going Pro", html);
        Assert.DoesNotContain(ThanksCopy, html);
    }

    [Fact]
    public void TextFooter_CarriesSocialLinksAndTheSupporterLine()
    {
        var ask = OnboardingTemplates.TextFooter(Footer with
        {
            Supporter = SupporterLine.Ask,
            SubscriptionUrl = "https://www.3dprintlog.test/subscription",
        });
        var none = OnboardingTemplates.TextFooter(Footer);

        Assert.Contains("YouTube: https://www.youtube.com/@hoffmanengineering", ask);
        Assert.Contains("GitHub: https://github.com/HoffmanEngineering/3d-print-log-ui", ask);
        Assert.Contains("Blog: https://hoffman.engineering/", ask);
        Assert.Contains(AskCopy, ask);
        Assert.Contains("Go Pro: https://www.3dprintlog.test/subscription", ask);
        Assert.DoesNotContain(AskCopy, none);
    }
}
