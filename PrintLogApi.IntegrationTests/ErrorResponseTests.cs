using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PrintLogApi.Authentication;
using PrintLogApi.IntegrationTests.Mcp;
using PrintLogApi.Middleware;
using Xunit;

namespace PrintLogApi.IntegrationTests;

/// <summary>
/// Status-code-only responses (#127). An unknown path, a wrong method and an unauthenticated
/// request used to come back with an empty body and no content type, which leaves an agent
/// nothing to reason about. They now carry an RFC 7807 body, and a REST 401 says where its
/// protected-resource metadata lives. Both are additive: none of these responses had a body,
/// so nothing can have been parsing one. <c>/mcp</c> keeps its own contract untouched.
/// </summary>
public class ErrorResponseTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string ProblemJson = "application/problem+json";

    private readonly CustomWebApplicationFactory _factory;

    public ErrorResponseTests(CustomWebApplicationFactory factory) => _factory = factory;

    /// <summary>The configured public base URL, which is what production advertises.</summary>
    private string PublicBaseUrl => _factory.Services.GetRequiredService<IConfiguration>()["OpenApi:ServerUrl"]!.TrimEnd('/');

    private string ExpectedResourceMetadataUrl => PublicBaseUrl + RestProtectedResource.MetadataPath;

    private static async Task<JsonElement> ReadProblem(HttpResponseMessage response)
    {
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static void AssertOnlyStandardMembers(JsonElement problem)
    {
        // No detail beyond these: the no-leak decisions elsewhere in the API still hold.
        var allowed = new[] { "type", "title", "status", "detail", "traceId" };
        foreach (var property in problem.EnumerateObject())
        {
            Assert.Contains(property.Name, allowed);
        }
    }

    [Theory]
    [InlineData("/nope-xyz")]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    [InlineData("/.well-known/oauth-authorization-server")]
    public async Task UnknownRoute_Returns404ProblemDetails_PointingAtTheDocs(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblem(response);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("type").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));

        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains(ProblemDetailsStatusCodePages.DocumentationUrl, detail);
        Assert.Contains(PublicBaseUrl + "/swagger/v1/swagger.json", detail);
        AssertOnlyStandardMembers(problem);
    }

    [Fact]
    public async Task UnknownRoute_StillReturnsProblemDetails_WhenTheClientAsksForHtml()
    {
        // Agents send all sorts of Accept headers. A JSON error body is more useful to every
        // one of them than an empty body, which is what content negotiation would fall back to.
        var request = new HttpRequestMessage(HttpMethod.Get, "/nope-xyz");
        request.Headers.Accept.ParseAdd("text/html");

        var response = await _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblem(response);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task WrongMethod_Returns405ProblemDetails()
    {
        // PrintersController has a POST at its root and no GET.
        var response = await _factory.CreateClient().GetAsync("/api/printers", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("POST", response.Content.Headers.Allow);

        var problem = await ReadProblem(response);
        Assert.Equal(405, problem.GetProperty("status").GetInt32());
        Assert.Contains("GET", problem.GetProperty("detail").GetString());
        AssertOnlyStandardMembers(problem);
    }

    [Fact]
    public async Task RestEndpoint_WithoutCredentials_Returns401ProblemDetails_AndAdvertisesResourceMetadata()
    {
        var response = await _factory.CreateClient().GetAsync("/api/Users/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains($"resource_metadata=\"{ExpectedResourceMetadataUrl}\"", challenge.Parameter);

        var problem = await ReadProblem(response);
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains("X-Api-Key", detail);
        Assert.Contains("resource_metadata", detail);
        AssertOnlyStandardMembers(problem);
    }

    [Fact]
    public async Task RestEndpoint_WithAnInvalidBearerToken_KeepsTheErrorAndAddsResourceMetadata()
    {
        // The probe endpoint is guarded by the real JwtBearer scheme, so this is the challenge
        // production sends: the framework's own error parameters must survive the append.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/web-auth-probe");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains("error=\"invalid_token\"", challenge.Parameter);
        Assert.Contains($"resource_metadata=\"{ExpectedResourceMetadataUrl}\"", challenge.Parameter);
    }

    [Fact]
    public async Task RestEndpoint_WithAnMcpAudienceToken_AdvertisesTheRestMetadata()
    {
        // The case the REST-specific metadata exists for: an agent holding an MCP token that
        // tries the REST API must be pointed at the REST audience, not back at the MCP one.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/web-auth-probe");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestJwt.Create(TestJwt.McpAudience, scopes: ["read:printdata"]));

        var response = await _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Contains($"resource_metadata=\"{ExpectedResourceMetadataUrl}\"", challenge.Parameter);
    }

    [Fact]
    public async Task InvalidApiKey_KeepsItsExistingBody_AndAdvertisesResourceMetadata()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/UserApiKeys");
        request.Headers.Add("X-Api-Key", "INVALIDKEY00000000000000000000000");

        var response = await _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // An existing body is #73's to change, not this issue's.
        Assert.Equal("Invalid API Key", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains($"resource_metadata=\"{ExpectedResourceMetadataUrl}\"", challenge.Parameter);
    }

    [Fact]
    public async Task RestResourceMetadata_DescribesTheRestAudience_NotTheMcpOne()
    {
        var response = await _factory.CreateClient().GetAsync(RestProtectedResource.MetadataPath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = doc.RootElement;

        Assert.Equal(TestJwt.ApiAudience, root.GetProperty("resource").GetString());
        Assert.Equal([TestJwt.Issuer], root.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["header"], root.GetProperty("bearer_methods_supported").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(ProblemDetailsStatusCodePages.DocumentationUrl, root.GetProperty("resource_documentation").GetString());

        // REST checks audience only; the data scopes exist on the MCP audience alone. Listing
        // them here would describe a check the API does not make (see the OpenAPI notes).
        Assert.False(root.TryGetProperty("scopes_supported", out _));
    }

    [Fact]
    public async Task McpWithoutToken_IsExactlyTodaysResponse()
    {
        // /mcp has its own error contract, and its 401 header drives MCP OAuth discovery, so
        // neither the body nor the header may change.
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            $"Bearer resource_metadata=\"{new Uri(client.BaseAddress!, "/.well-known/oauth-protected-resource/mcp")}\"",
            response.Headers.WwwAuthenticate.ToString());
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownPathUnderMcp_IsLeftAlone()
    {
        var response = await _factory.CreateClient().GetAsync("/mcp/nope", TestContext.Current.CancellationToken);

        Assert.NotEqual(ProblemJson, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ExistingProblemDetailsBodies_AreNotRewritten()
    {
        // Model validation already returns problem+json. It must come back exactly as MVC
        // shapes it: no detail added, and its errors dictionary intact.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys")
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblem(response);
        Assert.True(problem.TryGetProperty("errors", out _));
        Assert.False(problem.TryGetProperty("detail", out _));
    }
}
