using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PrintLogApi.SpoolTags;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

/// <summary>GET /api/FilamentTags/reader-config: the key-derivation schemes the native app uses to read tags.</summary>
public class ReaderConfigEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string Url = "/api/FilamentTags/reader-config";

    private readonly CustomWebApplicationFactory _factory;

    public ReaderConfigEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestMessage Authed()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return request;
    }

    private static TagReaderOptions Options(params TagReaderFormat[] formats) => new() { Formats = [.. formats] };

    private static TagReaderFormat Format(string? id, string? scheme, params (string Key, string Value)[] values) => new()
    {
        Id = id,
        Scheme = scheme,
        Params = values.ToDictionary(v => v.Key, v => v.Value),
    };

    private static string? Validate(TagReaderOptions options)
    {
        var result = new TagReaderOptionsValidator().Validate(null, options);
        return result.Failed ? result.FailureMessage : null;
    }

    [Fact]
    public async Task ReturnsTheConfiguredFormatVerbatim()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(Authed(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromHours(1), response.Headers.CacheControl?.MaxAge);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var format = Assert.Single(body.GetProperty("formats").EnumerateArray());
        Assert.Equal("snapmaker", format.GetProperty("id").GetString());
        Assert.Equal("hmac-sha256-sector", format.GetProperty("scheme").GetString());
        var values = format.GetProperty("params");
        Assert.Equal("00112233", values.GetProperty("saltHex").GetString());
        Assert.Equal("label-", values.GetProperty("labelPrefix").GetString());
    }

    [Fact]
    public async Task Unauthenticated_ReturnsUnauthorized()
    {
        var response = await _factory.CreateClient().GetAsync(Url, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NoFormatsConfigured_ReturnsAnEmptyList()
    {
        using var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Configure<TagReaderOptions>(o => o.Formats.Clear())));

        var response = await factory.CreateClient().SendAsync(Authed(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal(0, body.GetProperty("formats").GetArrayLength());
    }

    [Fact]
    public void UnknownScheme_FailsAndNamesTheFormat()
    {
        var message = Validate(Options(Format("acme", "rot13")));

        Assert.NotNull(message);
        Assert.Contains("acme", message);
        Assert.Contains("rot13", message);
    }

    [Fact]
    public void BindingFromConfiguration_AndValidating_NamesTheFormat()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TagReader:Formats:0:Id"] = "acme",
            ["TagReader:Formats:0:Scheme"] = "rot13",
        }).Build();
        var services = new ServiceCollection();
        services.AddTagReaderOptions(config);
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TagReaderOptions>>().Value);

        Assert.Contains("acme", ex.Message);
    }

    [Fact]
    public void ValidFormats_Pass()
    {
        Assert.Null(Validate(Options(
            Format("a", "hmac-sha256-sector", ("saltHex", "00112233"), ("labelPrefix", "k_")),
            Format("b", "hkdf-sha256-sectors", ("saltHex", "0011"), ("infoHex", "aabb"), ("sectors", "5")))));
        Assert.Null(Validate(Options()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingId_Fails(string? id)
    {
        Assert.NotNull(Validate(Options(Format(id, "hmac-sha256-sector", ("saltHex", "00"), ("labelPrefix", "k")))));
    }

    [Fact]
    public void DuplicateIds_FailCaseInsensitively()
    {
        var message = Validate(Options(
            Format("snap", "hmac-sha256-sector", ("saltHex", "00"), ("labelPrefix", "k")),
            Format("SNAP", "hmac-sha256-sector", ("saltHex", "00"), ("labelPrefix", "k"))));

        Assert.NotNull(message);
        Assert.Contains("SNAP", message);
    }

    [Theory]
    [InlineData("hmac-sha256-sector", "saltHex")]
    [InlineData("hmac-sha256-sector", "labelPrefix")]
    [InlineData("hkdf-sha256-sectors", "saltHex")]
    [InlineData("hkdf-sha256-sectors", "infoHex")]
    public void MissingParam_FailsAndNamesTheFormat(string scheme, string missing)
    {
        var all = new Dictionary<string, string>
        {
            ["saltHex"] = "00",
            ["labelPrefix"] = "k",
            ["infoHex"] = "00",
        };
        all.Remove(missing);
        var format = new TagReaderFormat { Id = "acme", Scheme = scheme, Params = all };

        var message = Validate(Options(format));

        Assert.NotNull(message);
        Assert.Contains("acme", message);
        Assert.Contains(missing, message);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("zz")]
    [InlineData("")]
    public void BadHex_Fails(string hex)
    {
        var message = Validate(Options(Format("acme", "hkdf-sha256-sectors", ("saltHex", hex), ("infoHex", "00"))));

        Assert.NotNull(message);
        Assert.Contains("acme", message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("17")]
    [InlineData("x")]
    public void SectorsOutOfRange_Fails(string sectors)
    {
        var message = Validate(Options(
            Format("acme", "hmac-sha256-sector", ("saltHex", "00"), ("labelPrefix", "k"), ("sectors", sectors))));

        Assert.NotNull(message);
        Assert.Contains("acme", message);
    }

    [Fact]
    public void ParamKeys_AreCaseInsensitive_InTheValidator()
    {
        Assert.Null(Validate(Options(
            Format("a", "hmac-sha256-sector", ("SaltHex", "00112233"), ("LABELPREFIX", "k_"), ("Sectors", "5")))));

        var message = Validate(Options(
            Format("b", "hmac-sha256-sector", ("SaltHex", "00112233"), ("LabelPrefix", "k_"), ("Sectors", "99"))));

        Assert.NotNull(message);
        Assert.Contains("sectors", message);
    }

    [Fact]
    public async Task ParamKeys_AreServedUnderTheirCanonicalNames()
    {
        using var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Configure<TagReaderOptions>(o =>
            {
                o.Formats.Clear();
                o.Formats.Add(Format("acme", "hmac-sha256-sector", ("SALTHEX", "0011"), ("labelprefix", "k_"), ("Sectors", "3")));
            })));

        var response = await factory.CreateClient().SendAsync(Authed(), Ct);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var values = body.GetProperty("formats")[0].GetProperty("params");
        Assert.Equal("0011", values.GetProperty("saltHex").GetString());
        Assert.Equal("k_", values.GetProperty("labelPrefix").GetString());
        Assert.Equal("3", values.GetProperty("sectors").GetString());
    }
}
