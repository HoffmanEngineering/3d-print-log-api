using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

/// <summary>
/// Writes every fixture email to <c>artifacts/email-preview/</c> with links and images pointed at
/// the local UI dev server, for checking layout in a browser before a real send. Opt-in:
/// `EMAIL_PREVIEW=1` (and optionally `EMAIL_PREVIEW_BASE`, default https://localhost:4200).
/// </summary>
public class EmailPreviewWriter : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EmailPreviewWriter(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task WritePreviews()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EMAIL_PREVIEW") == "1", "Set EMAIL_PREVIEW=1 to write email previews.");

        var baseUrl = Environment.GetEnvironmentVariable("EMAIL_PREVIEW_BASE") ?? "https://localhost:4200";
        var folder = Path.Combine(RepoRoot(), "artifacts", "email-preview");
        Directory.CreateDirectory(folder);

        foreach (var fixture in EmailFixtures.All)
        {
            var (html, text) = await fixture.Render(_factory.Services);
            await File.WriteAllTextAsync(Path.Combine(folder, fixture.Name + ".html"), html.Replace("https://www.3dprintlog.test", baseUrl), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(folder, fixture.Name + ".txt"), text.Replace("https://www.3dprintlog.test", baseUrl), TestContext.Current.CancellationToken);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PrintLogApi.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("PrintLogApi.sln not found above the test output.");
    }
}
