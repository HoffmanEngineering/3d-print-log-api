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
    public async Task Description_LinksTheApiAndAuthenticationDocs()
    {
        // The documentation the ProblemDetails bodies and the REST protected-resource metadata
        // point to (#127), and the agent-facing auth.md (UI #209). An agent that starts from this
        // document should reach the same pages.
        var document = await LoadDocument();

        var description = document.GetProperty("info").GetProperty("description").GetString();
        Assert.Contains("https://www.3dprintlog.com/docs/api", description);
        Assert.Contains("https://www.3dprintlog.com/auth.md", description);
    }

    [Fact]
    public async Task Description_DocumentsTheConnectorWriteLimit()
    {
        // Connectors generate clients and tool definitions from this document, so the write
        // budget they will hit, and how to back off from it, belongs here as well as on the site
        // (#151).
        var document = await LoadDocument();

        var description = document.GetProperty("info").GetProperty("description").GetString();
        Assert.Contains("## Rate limits", description);
        Assert.Contains("60 per minute", description);
        Assert.Contains("burst of 20", description);
        Assert.Contains("Retry-After", description);
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
    [InlineData("/api/Users/me/onboarding", "get", new[] { "oauth2" }, false)]
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

    [Theory]
    // The three the integrator spec named (#152); the filter covers every integer enum.
    [InlineData("PrintLogApi.Models.Print_PrintStatus", new[] { 1, 2, 3, 4, 5, 6 }, new[] { "Pending", "Printing", "Success", "Cancelled", "Failed", "PartialSuccess" })]
    [InlineData("PrintLogApi.Models.Print_PrintViewStatus", new[] { 1, 2, 3 }, new[] { "Public", "Unlisted", "Private" })]
    [InlineData("PrintLogApi.Models.PrintFilament_SourceMeasurement", new[] { 1, 2, 3 }, new[] { "Weight", "Length", "Volume" })]
    public async Task Enums_StayIntegers_AndNameTheirMembers(string schemaName, int[] values, string[] names)
    {
        var document = await LoadDocument();
        var schema = document.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);

        // The wire format is unchanged: every client today sends and parses the numbers.
        Assert.Equal("integer", schema.GetProperty("type").GetString());
        Assert.Equal(values, schema.GetProperty("enum").EnumerateArray().Select(v => v.GetInt32()));

        // NSwag and openapi-typescript read the first, openapi-generator the second.
        Assert.Equal(names, schema.GetProperty("x-enumNames").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(names, schema.GetProperty("x-enum-varnames").EnumerateArray().Select(v => v.GetString()));
        Assert.Contains($"`{values[0]}` = {names[0]}", schema.GetProperty("description").GetString());
    }

    [Fact]
    public async Task EveryIntegerEnum_NamesItsMembers()
    {
        var document = await LoadDocument();

        var unnamed = document.GetProperty("components").GetProperty("schemas").EnumerateObject()
            .Where(s => s.Value.TryGetProperty("enum", out _) && s.Value.GetProperty("type").GetString() == "integer")
            .Where(s => !s.Value.TryGetProperty("x-enumNames", out var n)
                || n.GetArrayLength() != s.Value.GetProperty("enum").GetArrayLength())
            .Select(s => s.Name)
            .ToList();

        Assert.Empty(unnamed);
    }

    [Fact]
    public async Task MoonrakerNotifier_DocumentsItsJsonBody()
    {
        // The action reads Request.Body itself, so ApiExplorer had no body to describe.
        var document = await LoadDocument();
        var operation = document.GetProperty("paths").GetProperty("/api/Moonraker/notifier").GetProperty("post");

        var body = operation.GetProperty("requestBody");
        Assert.True(body.GetProperty("required").GetBoolean());
        Assert.Equal(
            "#/components/schemas/PrintLogApi.Models.DTOs.Moonraker.PrintEventDto",
            body.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());

        // The event inside `message` is described by the response, which echoes it decoded.
        Assert.Equal(
            "#/components/schemas/PrintLogApi.Models.DTOs.Moonraker.PrintEventMessageDto",
            operation.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());

        var message = document.GetProperty("components").GetProperty("schemas")
            .GetProperty("PrintLogApi.Models.DTOs.Moonraker.PrintEventMessageDto").GetProperty("properties");
        Assert.True(message.TryGetProperty("event_name", out _));
        Assert.True(message.TryGetProperty("printerId", out _));
    }

    [Theory]
    [InlineData("Extra", "PrintLogApi.Models.DTOs.Octoprint.OctoprintWebhookExtraDto")]
    [InlineData("Job", "PrintLogApi.Models.DTOs.Octoprint.OctoprintWebhookJobDto")]
    [InlineData("Meta", "PrintLogApi.Models.DTOs.Octoprint.OctoprintWebhookMetaDto")]
    public async Task OctoprintWebhook_DocumentsItsJsonFormParts(string field, string schemaName)
    {
        // Fields bound with JsonModelBinder were missing from the form schema entirely.
        var document = await LoadDocument();
        var form = document.GetProperty("paths").GetProperty("/api/Octoprint").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data");

        Assert.Equal(
            $"#/components/schemas/{schemaName}",
            form.GetProperty("schema").GetProperty("properties").GetProperty(field).GetProperty("$ref").GetString());
        Assert.Equal("application/json", form.GetProperty("encoding").GetProperty(field).GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task SlicerSettings_DocumentsItsFieldsAndLimits()
    {
        // #164: connectors generate their client from this document.
        var document = await LoadDocument();
        var operation = document.GetProperty("paths").GetProperty("/api/Prints/{id}/slicer-settings").GetProperty("post");

        Assert.Equal("Prints_PostSlicerSettings", operation.GetProperty("operationId").GetString());
        var responses = operation.GetProperty("responses");
        foreach (var status in new[] { "200", "400", "404", "409", "413" })
        {
            Assert.True(responses.TryGetProperty(status, out _), $"no {status} response");
        }

        var schemas = document.GetProperty("components").GetProperty("schemas");
        var body = schemas.GetProperty("PrintLogApi.Models.DTOs.Print.PrintSlicerSettingsDto").GetProperty("properties");
        Assert.Contains("512 KiB", body.GetProperty("configBlock").GetProperty("description").GetString());
        Assert.Contains("HEADER_BLOCK_END", body.GetProperty("header").GetProperty("description").GetString());
        Assert.True(body.TryGetProperty("metadata", out _));
        // A computed helper, not a field a caller sends.
        Assert.False(body.TryGetProperty("hasConfigBlock", out _));

        var metadata = schemas.GetProperty("PrintLogApi.Models.DTOs.Print.SlicerMetadataDto").GetProperty("properties");
        foreach (var field in new[]
        {
            "slicer", "slicerVersion", "layerHeightMm", "firstLayerHeightMm", "nozzleDiameterMm",
            "filamentName", "filamentType", "firstLayerNozzleTemperatureC", "firstLayerBedTemperatureC",
        })
        {
            Assert.True(metadata.TryGetProperty(field, out var property), $"metadata.{field} is not documented");
            Assert.True(property.TryGetProperty("description", out _), $"metadata.{field} has no description");
        }
    }

    [Fact]
    public async Task CuraSetting_DoesNotDocumentItsPrintLink()
    {
        // #164's CuraSetting.PrintId/Print are server-side only; the plugins' schema is unchanged.
        var document = await LoadDocument();
        var properties = document.GetProperty("components").GetProperty("schemas")
            .GetProperty("PrintLogApi.Models.CuraSetting").GetProperty("properties");

        Assert.False(properties.TryGetProperty("printId", out _));
        Assert.False(properties.TryGetProperty("print", out _));
    }

    [Fact]
    public async Task JsonRequestBodies_AreDocumentedAsApplicationJsonOnly()
    {
        // openapi-python-client skips any operation whose body also lists text/json or
        // application/*+json, and that was 33 of them.
        var document = await LoadDocument();

        var aliased = Operations(document)
            .Where(o => o.Operation.TryGetProperty("requestBody", out var body)
                && body.GetProperty("content").EnumerateObject().Any(c => c.Name is "text/json" or "application/*+json"))
            .Select(o => $"{o.Method.ToUpperInvariant()} {o.Path}")
            .ToList();

        Assert.Empty(aliased);
    }

    [Fact]
    public async Task EstimatedVolumeMl_IsDescribedAsAVolume()
    {
        // Was copied from the length field above it: "The estimated length of filament used in meters."
        var document = await LoadDocument();

        var description = document.GetProperty("components").GetProperty("schemas")
            .GetProperty("PrintLogApi.Models.DTOs.Print.PrintFilamentSummaryDto").GetProperty("properties")
            .GetProperty("estimatedVolumeMl").GetProperty("description").GetString();
        Assert.Equal("The estimated volume of filament used in milliliters.", description);
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
