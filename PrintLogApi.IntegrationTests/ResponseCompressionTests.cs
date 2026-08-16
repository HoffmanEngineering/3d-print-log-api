using System.IO.Compression;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using Xunit;

namespace PrintLogApi.IntegrationTests;

/// <summary>
/// Covers the response compression added for issue #66.
///
/// The behavioural assertions all go through a real JSON endpoint rather than inspecting
/// options, because the thing worth pinning is what a client receives — that brotli wins the
/// negotiation, that gzip is still available to a client that cannot do brotli, that a client
/// asking for neither still gets a readable body, and that the compressed bytes decode back to
/// exactly the uncompressed response. Middleware ordering is easy to get wrong in a way that
/// only shows up as one of those four failing.
///
/// Note the test client never sets Accept-Encoding on its own and does not auto-decompress, so
/// every case here is explicit.
/// </summary>
public class ResponseCompressionTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string SummaryPath = "/api/Printers/summary";

    private readonly HttpClient _httpClient;
    private readonly CustomWebApplicationFactory _factory;

    public ResponseCompressionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    private HttpRequestMessage Request(params string[] acceptEncodings)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, SummaryPath);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        foreach (var encoding in acceptEncodings)
        {
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));
        }

        return request;
    }

    [Fact]
    public async Task JsonResponse_WhenClientAcceptsBrotliAndGzip_IsCompressedWithBrotli()
    {
        var response = await _httpClient.SendAsync(Request("br", "gzip"));

        response.EnsureSuccessStatusCode();
        // Brotli is registered first precisely so it wins this negotiation.
        Assert.Equal("br", Assert.Single(response.Content.Headers.ContentEncoding));
    }

    [Fact]
    public async Task JsonResponse_WhenClientAcceptsOnlyGzip_IsCompressedWithGzip()
    {
        var response = await _httpClient.SendAsync(Request("gzip"));

        response.EnsureSuccessStatusCode();
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
    }

    [Fact]
    public async Task JsonResponse_WhenClientAcceptsNoEncoding_IsNotCompressed()
    {
        var response = await _httpClient.SendAsync(Request());

        response.EnsureSuccessStatusCode();
        Assert.Empty(response.Content.Headers.ContentEncoding);
        // A client that asked for nothing must still be able to read the body directly.
        Assert.StartsWith("{", (await response.Content.ReadAsStringAsync()).TrimStart());
    }

    [Fact]
    public async Task CompressedResponse_AdvertisesVaryAcceptEncoding()
    {
        var response = await _httpClient.SendAsync(Request("br"));

        response.EnsureSuccessStatusCode();
        // Without this a shared cache could hand brotli bytes to a client that never asked for
        // them. The middleware sets it; the assertion is here so a future reordering that loses
        // it fails loudly.
        Assert.Contains(response.Headers.Vary, v => v.Contains("Accept-Encoding", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task CompressedResponse_DecodesToTheUncompressedBody(string encoding)
    {
        var plain = await _httpClient.SendAsync(Request());
        plain.EnsureSuccessStatusCode();
        var expected = await plain.Content.ReadAsStringAsync();

        var compressed = await _httpClient.SendAsync(Request(encoding));
        compressed.EnsureSuccessStatusCode();
        Assert.Equal(encoding, Assert.Single(compressed.Content.Headers.ContentEncoding));

        await using var raw = await compressed.Content.ReadAsStreamAsync();
        await using Stream decoder = encoding == "br"
            ? new BrotliStream(raw, CompressionMode.Decompress)
            : new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new StreamReader(decoder);

        Assert.Equal(expected, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Compression_AppliesOverHttps()
    {
        // The assertion that matters, and the one the other tests cannot make: every other case
        // here runs over http://localhost, where compression happens regardless of the
        // EnableForHttps setting. Production is entirely HTTPS (UseHttpsRedirection), so a
        // regression that reverted EnableForHttps to its framework default would leave all nine
        // other tests green while compression silently never ran for a single real user.
        // Pointing the client at an https base address makes HttpContext.Request.IsHttps true,
        // which is the exact condition the option gates on.
        var httpsClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        var request = new HttpRequestMessage(HttpMethod.Get, SummaryPath);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

        var response = await httpsClient.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.True(response.RequestMessage!.RequestUri!.Scheme == Uri.UriSchemeHttps);
        Assert.Equal("br", Assert.Single(response.Content.Headers.ContentEncoding));
    }

    [Fact]
    public void Compression_UsesFastestOnBothProviders()
    {
        var brotli = _factory.Services.GetRequiredService<IOptions<BrotliCompressionProviderOptions>>().Value;
        var gzip = _factory.Services.GetRequiredService<IOptions<GzipCompressionProviderOptions>>().Value;

        // Levels are a CPU-per-request decision, not a preference. Brotli SmallestSize measured
        // 600x the CPU of Fastest for three percentage points of size, and gzip is the codec an
        // attacker names when they want the expensive path — see the measurements in
        // Startup.ConfigureResponseCompression before raising either.
        Assert.Equal(CompressionLevel.Fastest, brotli.Level);
        Assert.Equal(CompressionLevel.Fastest, gzip.Level);
    }

    [Fact]
    public void Compression_DoesNotCoverServerSentEvents()
    {
        var options = _factory.Services.GetRequiredService<IOptions<ResponseCompressionOptions>>().Value;

        // Compressing a streaming body buffers it. /mcp negotiates text/event-stream for its
        // Streamable HTTP responses, so adding this type would stall an agent's tool call until
        // the response completed. Asserted on the options rather than over the wire because a
        // regression here is a one-line MimeTypes edit, and this fails on that edit directly.
        Assert.DoesNotContain("text/event-stream", options.MimeTypes);
    }

    [Fact]
    public void Compression_CoversProblemDetailsResponses()
    {
        var options = _factory.Services.GetRequiredService<IOptions<ResponseCompressionOptions>>().Value;

        // The framework defaults cover application/json but not the content type MVC uses for
        // validation and error responses.
        Assert.Contains("application/problem+json", options.MimeTypes);
        Assert.Contains("application/json", options.MimeTypes);
    }
}
