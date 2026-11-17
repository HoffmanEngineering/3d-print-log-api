using Microsoft.Extensions.Options;
using PrintLogApi.Achievements;
using PrintLogApi.Email;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Templates;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

/// <summary>
/// Every image URL the API can put in an email answers 200 with a PNG. The UI generates and
/// serves these files, so this repo's CI can't see them: run it against production after a UI
/// deploy and before enabling email (`EMAIL_ASSET_SMOKE=1`, optional `EMAIL_ASSET_BASE`).
/// </summary>
public class EmailAssetSmokeTests
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("EMAIL_ASSET_SMOKE") == "1";

    private static EmailAssets Assets() => new(Options.Create(new EmailOptions
    {
        WebBaseUrl = Environment.GetEnvironmentVariable("EMAIL_ASSET_BASE") ?? "https://www.3dprintlog.com",
    }));

    public static IEnumerable<string> Urls()
    {
        var assets = Assets();
        yield return assets.Wordmark;
        yield return assets.PrinterMark;
        yield return assets.SocialYouTube;
        yield return assets.SocialGitHub;
        yield return assets.SocialBlog;
        foreach (var def in AchievementCatalog.Definitions)
        {
            for (var tier = 1; tier <= def.Thresholds.Count; tier++)
            {
                yield return assets.Badge(BadgeImage.FileName(def, tier));
            }
        }
    }

    [Fact]
    public async Task EveryEmailImageIsServed()
    {
        Assert.SkipUnless(Enabled, "Set EMAIL_ASSET_SMOKE=1 to check the deployed email images.");

        using var http = new HttpClient();
        var missing = new List<string>();
        foreach (var url in Urls().Distinct())
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "image/png")
            {
                missing.Add($"{(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType} {url}");
            }
        }

        Assert.True(missing.Count == 0, "Not served as PNG:\n" + string.Join('\n', missing));
    }
}
