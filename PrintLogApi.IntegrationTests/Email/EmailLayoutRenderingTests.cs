using Microsoft.AspNetCore.Components;
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

    private Task<string> RenderAsync()
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
            [nameof(EmailLayout.Footer)] = Footer,
            [nameof(EmailLayout.ChildContent)] = child,
        });
    }

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
}
