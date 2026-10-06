using PrintLogApi.Mcp.Docs;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp.Docs;

/// <summary>The index parser and the slug rule, which are what keep a fetch on the configured origin.</summary>
public class DocsIndexTests
{
    private static readonly Uri Origin = new("https://www.3dprintlog.com");

    [Fact]
    public void ParsesTheGeneratedFormat()
    {
        var pages = DocsIndex.Parse("""
            # 3D Print Log Docs

            ## Integrations

            - [Log prints from Klipper](https://www.3dprintlog.com/docs/klipper.md): Automatically log prints from Klipper.
            - [REST API & Authentication](https://www.3dprintlog.com/docs/api.md): Call the REST API.

            ## Optional

            - [Home](https://www.3dprintlog.com/index.md): What 3D Print Log is.
            """.Replace("\n", "\r\n"), Origin, maxPages: 100);

        Assert.Equal(2, pages.Count);
        var klipper = pages[0];
        Assert.Equal("klipper", klipper.Slug);
        Assert.Equal("Log prints from Klipper", klipper.Title);
        Assert.Equal("Automatically log prints from Klipper.", klipper.Description);
        Assert.Equal("Integrations", klipper.Section);
        Assert.Equal("https://www.3dprintlog.com/docs/klipper", klipper.Url);
        Assert.Equal("https://www.3dprintlog.com/docs/klipper.md", klipper.MarkdownUrl);
        Assert.Equal("docs://klipper", klipper.ResourceUri);
        Assert.Equal("REST API & Authentication", pages[1].Title);
    }

    [Theory]
    [InlineData("https://evil.example/docs/klipper.md")]
    [InlineData("http://www.3dprintlog.com/docs/klipper.md")]
    [InlineData("https://www.3dprintlog.com:8443/docs/klipper.md")]
    [InlineData("https://user@www.3dprintlog.com/docs/klipper.md")]
    [InlineData("https://www.3dprintlog.com/docs/klipper.md?x=1")]
    [InlineData("https://www.3dprintlog.com/docs/klipper.md#top")]
    [InlineData("https://www.3dprintlog.com/docs/../secrets.md")]
    [InlineData("https://www.3dprintlog.com/docs/a/b.md")]
    [InlineData("https://www.3dprintlog.com/docs/Klipper.md")]
    [InlineData("https://www.3dprintlog.com/docs/klipper.txt")]
    [InlineData("https://www.3dprintlog.com/docs/.md")]
    [InlineData("https://www.3dprintlog.com/docs/-x.md")]
    [InlineData("https://www.3dprintlog.com/docs/x--y.md")]
    [InlineData("/docs/klipper.md")]
    public void IgnoresLinksOffTheDocsOrigin(string link)
    {
        Assert.Empty(DocsIndex.Parse($"- [X]({link}): y", Origin, maxPages: 100));
    }

    [Fact]
    public void KeepsTheFirstOfADuplicate_AndHonorsTheCap()
    {
        var text = string.Join('\n',
            "- [One](https://www.3dprintlog.com/docs/one.md): first",
            "- [Again](https://www.3dprintlog.com/docs/one.md): second",
            "- [Two](https://www.3dprintlog.com/docs/two.md): x",
            "- [Three](https://www.3dprintlog.com/docs/three.md): x");

        var pages = DocsIndex.Parse(text, Origin, maxPages: 2);

        Assert.Equal(new[] { "one", "two" }, pages.Select(p => p.Slug));
        Assert.Equal("first", pages[0].Description);
    }

    [Theory]
    [InlineData("klipper", true)]
    [InlineData("pro-subscription", true)]
    [InlineData("v2-api", true)]
    [InlineData("", false)]
    [InlineData("Klipper", false)]
    [InlineData("-klipper", false)]
    [InlineData("klipper-", false)]
    [InlineData("a--b", false)]
    [InlineData("../x", false)]
    [InlineData("a/b", false)]
    [InlineData("klipper.md", false)]
    [InlineData("klipper\n", false)]
    public void SlugRule(string slug, bool valid)
    {
        Assert.Equal(valid, DocsIndex.IsValidSlug(slug));
    }

    [Theory]
    [InlineData("docs://klipper", "klipper")]
    [InlineData("docs://../x", null)]
    [InlineData("docs://", null)]
    [InlineData("DOCS://klipper", null)]
    [InlineData("https://www.3dprintlog.com/docs/klipper", null)]
    [InlineData(null, null)]
    public void SlugFromResourceUri(string? uri, string? slug)
    {
        Assert.Equal(slug, DocsIndex.SlugFromResourceUri(uri));
    }

    [Fact]
    public void SearchTerms_DropStopWordsAndDuplicates()
    {
        Assert.Equal(new[] { "connect", "klipper" }, DocsSearch.Terms("How do I connect Klipper? klipper!"));
    }
}
