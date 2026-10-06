using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace PrintLogApi.IntegrationTests;

/// <summary>
/// Pins the generated OpenAPI document as valid and usable by agents (#126).
///
/// Agents turn this document into function-calling tools, where an id collision silently drops
/// or overwrites a tool, and validators reject the whole document over one bad component name.
/// Both happened in production. These assert the rules that broke; CI additionally lints the
/// document with Redocly, which the last test feeds.
/// </summary>
public partial class OpenApiDocumentTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private const string DocumentPath = "/swagger/v1/swagger.json";

    /// <summary>
    /// Set by CI to a file path. The document is written there for <c>redocly lint</c>.
    /// </summary>
    private const string OutputPathVariable = "OPENAPI_DOCUMENT_OUTPUT";

    private static readonly string[] HttpMethods = ["get", "put", "post", "delete", "patch", "head", "options", "trace"];

    [GeneratedRegex(@"^[a-zA-Z0-9\.\-_]+$")]
    private static partial Regex ComponentNamePattern();

    private async Task<string> FetchDocument()
    {
        var response = await factory.CreateClient().GetAsync(DocumentPath, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private async Task<JsonElement> LoadDocument() =>
        JsonDocument.Parse(await FetchDocument()).RootElement;

    private static IEnumerable<(string Path, string Method, JsonElement Operation)> Operations(JsonElement document) =>
        document.GetProperty("paths").EnumerateObject().SelectMany(path =>
            path.Value.EnumerateObject()
                .Where(op => HttpMethods.Contains(op.Name))
                .Select(op => (path.Name, op.Name, op.Value)));

    [Fact]
    public async Task EveryOperation_HasAUniqueOperationId()
    {
        var document = await LoadDocument();

        var missing = Operations(document)
            .Where(o => !o.Operation.TryGetProperty("operationId", out _))
            .Select(o => $"{o.Method.ToUpperInvariant()} {o.Path}")
            .ToList();
        Assert.Empty(missing);

        var duplicates = Operations(document)
            .GroupBy(o => o.Operation.GetProperty("operationId").GetString())
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(o => $"{o.Method.ToUpperInvariant()} {o.Path}"))}")
            .ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public async Task OperationIds_AreNamespacedByController()
    {
        var document = await LoadDocument();

        var ids = Operations(document).ToDictionary(
            o => $"{o.Method.ToUpperInvariant()} {o.Path}",
            o => o.Operation.GetProperty("operationId").GetString());

        // Three of the five former "GetMaterials" collisions.
        Assert.Equal("MaterialCategories_GetMaterials", ids["GET /api/MaterialCategories"]);
        Assert.Equal("Analytics_GetMaterials", ids["GET /api/Analytics/materials"]);
        Assert.Equal("Users_GetUserSummary", ids["GET /api/Users/{id}/summary"]);
        Assert.Equal("Users_GetCurrentUserDetails", ids["GET /api/Users/me"]);
    }

    [Fact]
    public async Task RouteAlias_ForMaterialTypes_IsNotDocumentedTwice()
    {
        var document = await LoadDocument();
        var paths = document.GetProperty("paths");

        Assert.True(paths.TryGetProperty("/api/MaterialTypes", out _));
        Assert.False(paths.TryGetProperty("/api/Materials", out _));

        // Undocumented is not removed: the web app still calls the alias.
        var response = await factory.CreateClient().GetAsync("/api/Materials", TestContext.Current.CancellationToken);
        Assert.NotEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EveryOperation_HasASummary()
    {
        var document = await LoadDocument();

        var missing = Operations(document)
            .Where(o => !o.Operation.TryGetProperty("summary", out var s) || string.IsNullOrWhiteSpace(s.GetString()))
            .Select(o => $"{o.Method.ToUpperInvariant()} {o.Path}")
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public async Task ComponentNames_AreValidOpenApiKeys()
    {
        var document = await LoadDocument();

        var invalid = document.GetProperty("components").EnumerateObject()
            .SelectMany(section => section.Value.EnumerateObject().Select(c => $"{section.Name}/{c.Name}"))
            .Where(name => !ComponentNamePattern().IsMatch(name.Split('/')[1]))
            .ToList();

        Assert.Empty(invalid);
    }

    [Fact]
    public async Task Servers_NameTheProductionApiBaseUrl()
    {
        // IntegrationTesting inherits OpenApi:ServerUrl from appsettings.json, which is the
        // production value.
        var document = await LoadDocument();

        var server = Assert.Single(document.GetProperty("servers").EnumerateArray());
        Assert.Equal("https://api.3dprintlog.com", server.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Servers_FallBackToTheRequestOrigin_WhenNoUrlIsConfigured()
    {
        // The local environments blank the setting, so Swagger UI's "Try it out" sends requests
        // to the API that served it rather than to production.
        using var unconfigured = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["OpenApi:ServerUrl"] = "" })));

        var response = await unconfigured.CreateClient().GetAsync(DocumentPath, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        var server = Assert.Single(document.GetProperty("servers").EnumerateArray());
        Assert.Equal("http://localhost", server.GetProperty("url").GetString());
    }

    [Fact]
    public async Task OAuthScheme_IsAuthorizationCode_NotImplicit()
    {
        var document = await LoadDocument();
        var schemes = document.GetProperty("components").GetProperty("securitySchemes");

        var flows = schemes.GetProperty("oauth2").GetProperty("flows");
        Assert.False(flows.TryGetProperty("implicit", out _));

        var code = flows.GetProperty("authorizationCode");
        Assert.Equal("https://test.auth0.com/authorize", code.GetProperty("authorizationUrl").GetString());
        Assert.Equal("https://test.auth0.com/oauth/token", code.GetProperty("tokenUrl").GetString());
        Assert.False(code.GetProperty("scopes").TryGetProperty("api1", out _));

        Assert.Equal("header", schemes.GetProperty("apikey").GetProperty("in").GetString());
        Assert.Equal("X-Api-Key", schemes.GetProperty("apikey").GetProperty("name").GetString());
        Assert.Equal("query", schemes.GetProperty("apikeyQuery").GetProperty("in").GetString());
        Assert.Equal("api_key", schemes.GetProperty("apikeyQuery").GetProperty("name").GetString());
    }

    [Theory]
    // Authenticated: a bearer token or either form of API key.
    [InlineData("/api/Printers/summary", "get", new[] { "oauth2", "apikey", "apikeyQuery" }, false)]
    // [AllowAnonymous]: public data without credentials, the owner's data with them.
    [InlineData("/api/Prints/{id}", "get", new[] { "oauth2", "apikey", "apikeyQuery" }, true)]
    // InteractiveUserOnly rejects API keys, so the document must not offer them.
    [InlineData("/api/Devices", "post", new[] { "oauth2" }, false)]
    [InlineData("/api/Users/me/email", "get", new[] { "oauth2" }, false)]
    public async Task SecurityRequirements_MatchTheEndpointsAuthorization(
        string path, string method, string[] schemes, bool allowsAnonymous)
    {
        var document = await LoadDocument();
        var operation = document.GetProperty("paths").GetProperty(path).GetProperty(method);

        var requirements = operation.GetProperty("security").EnumerateArray().ToList();
        Assert.Equal(allowsAnonymous, requirements.Any(r => !r.EnumerateObject().Any()));

        var named = requirements.SelectMany(r => r.EnumerateObject()).ToList();
        Assert.Equal(schemes.OrderBy(s => s), named.Select(r => r.Name).OrderBy(s => s));

        // The REST API checks no scope. Listing one would describe a check that is not made.
        Assert.All(named, r => Assert.Equal(0, r.Value.GetArrayLength()));

        Assert.Equal(!allowsAnonymous, operation.GetProperty("responses").TryGetProperty("401", out _));
    }

    [Fact]
    public async Task Document_IsWrittenForLinting_WhenCiAsksForIt()
    {
        var outputPath = Environment.GetEnvironmentVariable(OutputPathVariable);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(outputPath, await FetchDocument(), TestContext.Current.CancellationToken);
    }
}
