using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace PrintLogApi.IntegrationTests.Spoolman;

/// <summary>
/// Spoolman's web UI paths, which Fluidd links to off the <c>[spoolman]</c> server URL: "Manage
/// spools" opens the base, and the AFC panel opens <c>{base}/spool/show/{id}</c>. 3D Print Log has
/// no Spoolman UI, so both redirect to the web app's materials, anonymously, since a browser
/// following the link carries no credential.
/// </summary>
public class SpoolmanWebUiRedirectTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    // Email:WebBaseUrl in appsettings.IntegrationTesting.json.
    private const string Web = "https://www.3dprintlog.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Theory]
    [InlineData("/spoolman")]
    [InlineData("/spoolman/")]
    public async Task Base_RedirectsToMaterials(string path)
    {
        var response = await Client().GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(new Uri($"{Web}/materials"), response.Headers.Location);
    }

    [Fact]
    public async Task SpoolShow_RedirectsToTheSpoolNumber()
    {
        var response = await Client().GetAsync("/spoolman/spool/show/7", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(new Uri($"{Web}/materials/spool/7"), response.Headers.Location);
    }

    [Fact]
    public async Task Redirect_IgnoresAStaleCredential()
    {
        // The link carries the block's userinfo, which may since have been revoked.
        var request = new HttpRequestMessage(HttpMethod.Get, "/spoolman");
        request.Headers.Authorization = SpoolmanTestSupport.Basic("not-a-live-secret");

        var response = await Client().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task ApiRoot_IsStillNotFound()
    {
        var response = await Client().GetAsync("/spoolman/api/v1/", Ct);

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
    }
}
