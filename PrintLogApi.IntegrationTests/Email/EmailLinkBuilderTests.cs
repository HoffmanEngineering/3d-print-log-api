using PrintLogApi.Email;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Tokens;
using PrintLogApi.IntegrationTests.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailLinkBuilderTests
{
    private const string Key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    private static readonly EmailOptions Options = new()
    {
        WebBaseUrl = "https://www.3dprintlog.com/",
        ApiBaseUrl = "https://api.3dprintlog.com",
        PostalAddress = "PO Box 1, Somewhere, USA",
        UnsubscribeSigningKeys = [Key],
    };

    private static EmailLinkBuilder Links() => new(Microsoft.Extensions.Options.Options.Create(Options));

    [Fact]
    public void Web_AddsUtmParameters()
        => Assert.Equal(
            "https://www.3dprintlog.com/analytics?utm_source=email&utm_medium=email&utm_campaign=monthly-recap&utm_content=2026-11",
            Links().Web("/analytics", "monthly-recap", "2026-11"));

    [Fact]
    public void Web_PreservesExistingQueryAndFragment()
        => Assert.Equal(
            "https://www.3dprintlog.com/prints?status=failed&utm_source=email&utm_medium=email&utm_campaign=onboarding&utm_content=step-2#top",
            Links().Web("/prints?status=failed#top", "onboarding", "step-2"));

    // Fragments never reach a server log, a proxy or the Referer header; a query string does.
    [Fact]
    public void UnsubscribePage_CarriesTheTokenInAFragment()
        => Assert.Equal("https://www.3dprintlog.com/email-preferences#u=TOKEN", Links().UnsubscribePage("TOKEN"));

    [Fact]
    public void ManagePage_CarriesTheTokenInAFragment()
        => Assert.Equal("https://www.3dprintlog.com/email-preferences#m=TOKEN", Links().ManagePage("TOKEN"));

    [Fact]
    public void OneClick_PointsAtTheApi()
        => Assert.Equal("https://api.3dprintlog.com/api/email/unsubscribe?t=TOKEN", Links().OneClick("TOKEN"));

    [Fact]
    public void FooterFactory_BuildsRfc8058HeadersWithAValidToken()
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options);
        var tokens = new EmailTokenService(options, new SettableTimeProvider(DateTimeOffset.UtcNow));
        var factory = new EmailFooterFactory(Links(), tokens, options);

        var (footer, headers) = factory.Create(77, EmailSettingTypes.MonthlyRecap, "Because recaps are on.");

        Assert.Equal("List-Unsubscribe=One-Click", headers["List-Unsubscribe-Post"]);
        var match = System.Text.RegularExpressions.Regex.Match(headers["List-Unsubscribe"], "^<https://api\\.3dprintlog\\.com/api/email/unsubscribe\\?t=([^>]+)>$");
        Assert.True(match.Success, headers["List-Unsubscribe"]);
        Assert.True(tokens.TryValidate(match.Groups[1].Value, EmailTokenKind.Unsubscribe, out var payload));
        Assert.Equal(77, payload.UserId);
        Assert.Equal(EmailSettingTypes.MonthlyRecap, payload.Category);

        Assert.StartsWith("https://www.3dprintlog.com/email-preferences#u=", footer.UnsubscribeUrl);
        Assert.StartsWith("https://www.3dprintlog.com/email-preferences#m=", footer.ManageUrl);
        Assert.Equal("PO Box 1, Somewhere, USA", footer.PostalAddress);
        Assert.Equal("Because recaps are on.", footer.ReasonLine);
    }
}
