using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PrintLogApi.Models;
using PrintLogApi.PrinterCatalog;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class PrinterModelsControllerTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    [Fact]
    public async Task Search_ReturnsTheBestMatchFirst_ForAnAnonymousCaller()
    {
        var response = await factory.CreateClient().GetAsync("/api/PrinterModels?search=p1s", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var models = await JsonAsync(response);
        var first = models[0];
        Assert.Equal("bambu-lab-p1s", first.GetProperty("id").GetString());
        Assert.Equal("Bambu Lab", first.GetProperty("make").GetString());
        Assert.Equal("P1S", first.GetProperty("model").GetString());
        Assert.True(first.TryGetProperty("technology", out _));
        Assert.True(first.TryGetProperty("bedWidthMm", out _));
        Assert.True(first.TryGetProperty("nozzleOptionsMm", out _));
        Assert.True(first.TryGetProperty("defaultNozzleMm", out _));
        Assert.True(first.TryGetProperty("retired", out _));
    }

    [Fact]
    public async Task Search_ClampsALargeLimit_ToFifty()
    {
        var models = await JsonAsync(await factory.CreateClient().GetAsync("/api/PrinterModels?search=e&limit=999", Ct));

        Assert.Equal(50, models.GetArrayLength());
    }

    [Fact]
    public async Task Search_WithALimit_ReturnsThatMany()
    {
        var models = await JsonAsync(await factory.CreateClient().GetAsync("/api/PrinterModels?search=bambu&limit=3", Ct));

        Assert.Equal(3, models.GetArrayLength());
    }

    [Fact]
    public async Task Popular_ReturnsEightInFeaturedOrder_WhenNoCountsExist()
    {
        var models = await JsonAsync(await factory.CreateClient().GetAsync("/api/PrinterModels/popular", Ct));

        var expected = CustomWebApplicationFactory.PrinterCatalog.Models
            .Where(m => m.Featured != null).OrderBy(m => m.Featured).Take(8).Select(m => m.Id).ToList();
        Assert.Equal(8, models.GetArrayLength());
        Assert.Equal(expected, models.EnumerateArray().Select(m => m.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Popular_ClampsALargeLimit_ToFifty()
    {
        var models = await JsonAsync(await factory.CreateClient().GetAsync("/api/PrinterModels/popular?limit=999", Ct));

        Assert.InRange(models.GetArrayLength(), 1, 50);
    }

    [Fact]
    public async Task Popular_UsesTheConfiguredMinimumOwners()
    {
        using var host = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Catalog:PopularityMinOwners"] = "2" }));
        });
        host.Services.GetRequiredService<IPrinterCatalogIndex>().Rebuild(
        [
            Model("a-one", "Aaa", "One", owners: 1, featured: 1),
            Model("z-two", "Zzz", "Two", owners: 2, featured: 2),
        ]);

        var models = await JsonAsync(await host.CreateClient().GetAsync("/api/PrinterModels/popular", Ct));

        // Two owners clears the configured minimum of 2, so it outranks the featured order.
        Assert.Equal(["z-two", "a-one"], models.EnumerateArray().Select(m => m.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Get_ReturnsTheModel()
    {
        var response = await factory.CreateClient().GetAsync("/api/PrinterModels/bambu-lab-p1s", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var model = await JsonAsync(response);
        Assert.Equal("bambu-lab-p1s", model.GetProperty("id").GetString());
        Assert.False(model.GetProperty("retired").GetBoolean());
        Assert.Equal(JsonValueKind.Array, model.GetProperty("nozzleOptionsMm").ValueKind);
    }

    [Fact]
    public async Task Get_ResolvesARetiredModel_WithRetiredTrue_AndSearchNeverOffersIt()
    {
        using var host = HostWith(Model("old-printer", "Old", "Printer", retired: true));
        var client = host.CreateClient();

        var response = await client.GetAsync("/api/PrinterModels/old-printer", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("retired").GetBoolean());
        Assert.Equal(0, (await JsonAsync(await client.GetAsync("/api/PrinterModels?search=old", Ct))).GetArrayLength());
        Assert.Equal(0, (await JsonAsync(await client.GetAsync("/api/PrinterModels/popular", Ct))).GetArrayLength());
    }

    [Fact]
    public async Task Get_UnknownId_Is404WithACode()
    {
        var response = await factory.CreateClient().GetAsync("/api/PrinterModels/no-such-printer", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("printer_model_not_found", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Match_ResolvesAKnownName_AndIs204ForNoMatch()
    {
        var client = factory.CreateClient();

        var hit = await client.GetAsync("/api/PrinterModels/match?name=Bambu%20Lab%20P1S", Ct);
        var miss = await client.GetAsync("/api/PrinterModels/match?name=zzqx%20nothing", Ct);

        Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
        var body = await JsonAsync(hit);
        Assert.Equal("bambu-lab-p1s", body.GetProperty("id").GetString());
        Assert.Equal("exact", body.GetProperty("confidence").GetString());
        Assert.Equal(HttpStatusCode.NoContent, miss.StatusCode);
    }

    [Fact]
    public async Task Match_WithoutAName_Is204()
    {
        var response = await factory.CreateClient().GetAsync("/api/PrinterModels/match", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Attribution_ReturnsTheSources()
    {
        var response = await factory.CreateClient().GetAsync("/api/PrinterModels/attribution", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        var file = CustomWebApplicationFactory.PrinterCatalog;
        Assert.Equal(file.Attribution, body.GetProperty("attribution").GetString());
        var sources = body.GetProperty("sources");
        Assert.Equal(file.Sources.Count, sources.GetArrayLength());
        Assert.Equal(file.Sources[0].Repo, sources[0].GetProperty("repo").GetString());
        Assert.Equal(file.Sources[0].Tag, sources[0].GetProperty("tag").GetString());
        Assert.True(sources[0].TryGetProperty("commit", out _));
        Assert.True(sources[0].TryGetProperty("license", out _));
    }

    [Theory]
    [InlineData("/api/PrinterModels?search=p1s")]
    [InlineData("/api/PrinterModels/popular")]
    [InlineData("/api/PrinterModels/bambu-lab-p1s")]
    [InlineData("/api/PrinterModels/attribution")]
    public async Task Responses_CarryAnETag_AndAPublicCacheControl_AndAnswer304(string url)
    {
        var client = factory.CreateClient();

        var first = await client.GetAsync(url, Ct);
        var etag = first.Headers.ETag;

        Assert.NotNull(etag);
        Assert.Contains(factory.Services.GetRequiredService<IPrinterCatalogIndex>().Version, etag.Tag);
        Assert.True(first.Headers.CacheControl!.Public);
        Assert.Equal(TimeSpan.FromHours(1), first.Headers.CacheControl.MaxAge);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(etag);
        var second = await client.SendAsync(conditional, Ct);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task AStaleETag_GetsTheBody()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/PrinterModels/bambu-lab-p1s");
        request.Headers.IfNoneMatch.ParseAdd("\"stale\"");

        var response = await factory.CreateClient().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NoResponse_ExposesOwnerCounts()
    {
        var client = factory.CreateClient();
        foreach (var url in new[] { "/api/PrinterModels?search=prusa", "/api/PrinterModels/popular", "/api/PrinterModels/bambu-lab-p1s" })
        {
            var text = await (await client.GetAsync(url, Ct)).Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("ownerCount", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task BeforeTheFirstSeed_NothingIsCachedPublicly()
    {
        using var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton<IPrinterCatalogIndex>(new NotReadyIndex())));

        var response = await host.CreateClient().GetAsync("/api/PrinterModels?search=p1s", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.ETag);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.False(response.Headers.CacheControl.Public);
    }

    [Fact]
    public async Task APerAddressLimit_Answers429_OnceExceeded()
    {
        using var host = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Catalog:RateLimitPerMinute"] = "1" })));
        var client = host.CreateClient();

        var first = await client.GetAsync("/api/PrinterModels/popular", Ct);
        var second = await client.GetAsync("/api/PrinterModels/popular", Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    /// <summary>A host whose own index holds only these models. The factory has already built it.</summary>
    private WebApplicationFactory<Startup> HostWith(params PrinterModel[] models)
    {
        var host = factory.WithWebHostBuilder(_ => { });
        host.Services.GetRequiredService<IPrinterCatalogIndex>().Rebuild(models);
        return host;
    }

    private static PrinterModel Model(string id, string make, string model, int owners = 0, int? featured = null, bool retired = false) => new()
    {
        Id = id,
        Make = make,
        Model = model,
        Technology = "FDM",
        OwnerCount = owners,
        Featured = featured,
        Retired = retired,
    };

    private sealed class NotReadyIndex : IPrinterCatalogIndex
    {
        public string Version => "empty";
        public bool IsReady => false;
        public void Rebuild(IEnumerable<PrinterModel> models) { }
        public IReadOnlyList<PrinterModel> Search(string? query, int limit) => [];
        public IReadOnlyList<PrinterModel> Popular(int limit, int minOwners) => [];
        public PrinterModel? Get(string id) => null;
        public CatalogMatch? Match(string name) => null;
    }
}
