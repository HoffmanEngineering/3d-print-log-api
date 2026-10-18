using System.Net;
using System.Text.Json;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Sync;

/// <summary>
/// <c>POST /api/Filaments/{id}/adjustments</c> and <c>PUT /api/Filaments/{id}/active</c> (#236):
/// the two spool writes the Home Assistant integration makes. Both wrap the MCP service methods.
/// </summary>
public class FilamentIntegrationEndpointsTests(SyncTestFactory factory) : IClassFixture<SyncTestFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Filament> AddSpoolAsync(User user, long? initialMg = 1_000_000, long usedMg = 0, User? owner = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var owningUser = owner ?? user;
        var filament = new Filament
        {
            Id = Guid.NewGuid(),
            DisplayName = "Spool",
            Brand = "Prusament",
            MaterialType = "PLA",
            MaterialCategoryNickname = "filament",
            MaterialDensityGramPerCubicCm = 1.24,
            DiameterMm = 1.75,
            InitialNominalWeightMg = initialMg,
            IsActive = true,
            CreatedById = owningUser.Id,
            UpdatedById = owningUser.Id,
            CreatedDate = DateTime.UtcNow,
            UpdatedDate = DateTime.UtcNow,
        };
        db.Filaments.Add(filament);
        if (usedMg > 0)
        {
            var printer = new Printer { Name = "P", UserId = owningUser.Id, IsActive = true, CategoryNickname = "FDM" };
            db.Printers.Add(printer);
            await db.SaveChangesAsync(Ct);
            var print = new Print
            {
                Title = "Used",
                PrinterId = printer.Id,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
                CreatedById = owningUser.Id,
                UpdatedById = owningUser.Id,
            };
            db.Prints.Add(print);
            await db.SaveChangesAsync(Ct);
            db.PrintFilament.Add(new PrintFilament { PrintId = print.Id, FilamentId = filament.Id, AmountMg = (int)usedMg, Source = PrintFilament.SourceMeasurement.Weight });
        }

        await db.SaveChangesAsync(Ct);
        return filament;
    }

    private static HttpRequestMessage Adjust(User user, Guid id, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Filaments/{id}/adjustments") { Content = JsonContent.Create(body) };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        return request;
    }

    private static HttpRequestMessage SetActive(User user, Guid id, bool isActive)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Filaments/{id}/active") { Content = JsonContent.Create(new { isActive }) };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        return request;
    }

    private async Task<HttpResponseMessage> SnapshotAsync(User user, string? ifNoneMatch = null) =>
        await _client.SendAsync(factory.Get(user, "/api/sync/snapshot", ifNoneMatch), Ct);

    private static JsonElement SnapshotSpool(JsonElement snapshot, Guid id) =>
        snapshot.GetProperty("spools").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await JsonAsync(response);
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Adjust_Weight_ReturnsBeforeAfter()
    {
        var user = await factory.CreateUserAsync();
        var spool = await AddSpoolAsync(user, usedMg: 400_000);
        var before = await SnapshotAsync(user);

        var response = await _client.SendAsync(Adjust(user, spool.Id, new { source = "weight", delta = -50, notes = "dry run" }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(600, body.GetProperty("beforeGrams").GetDouble());
        Assert.Equal(550, body.GetProperty("afterGrams").GetDouble());
        var after = await SnapshotAsync(user);
        Assert.Equal(550, SnapshotSpool(await JsonAsync(after), spool.Id).GetProperty("remainingGrams").GetDouble());
        Assert.NotEqual(before.Headers.ETag!.ToString(), after.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task Adjust_SourceIsCaseInsensitive()
    {
        var user = await factory.CreateUserAsync();
        var spool = await AddSpoolAsync(user, usedMg: 400_000);

        var response = await _client.SendAsync(Adjust(user, spool.Id, new { source = "Weight", delta = 10 }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(610, (await JsonAsync(response)).GetProperty("afterGrams").GetDouble());
    }

    [Fact]
    public async Task Adjust_Untracked_Returns400()
    {
        var user = await factory.CreateUserAsync();
        var spool = await AddSpoolAsync(user, initialMg: null);

        await AssertProblemAsync(await _client.SendAsync(Adjust(user, spool.Id, new { source = "weight", delta = -5 }), Ct), HttpStatusCode.BadRequest, "material_untracked");
    }

    [Fact]
    public async Task Adjust_BelowZero_Returns400()
    {
        var user = await factory.CreateUserAsync();
        var spool = await AddSpoolAsync(user, usedMg: 400_000);

        await AssertProblemAsync(await _client.SendAsync(Adjust(user, spool.Id, new { source = "weight", delta = -700 }), Ct), HttpStatusCode.BadRequest, "adjustment_out_of_range");
    }

    [Fact]
    public async Task Adjust_ZeroDelta_Returns400()
    {
        var user = await factory.CreateUserAsync();
        var spool = await AddSpoolAsync(user);

        await AssertProblemAsync(await _client.SendAsync(Adjust(user, spool.Id, new { source = "weight", delta = 0 }), Ct), HttpStatusCode.BadRequest, "invalid_adjustment");
    }

    [Fact]
    public async Task Adjust_MissingDelta_Returns400()
    {
        var user = await factory.CreateUserAsync();
        var spool = await AddSpoolAsync(user);

        await AssertProblemAsync(await _client.SendAsync(Adjust(user, spool.Id, new { source = "weight" }), Ct), HttpStatusCode.BadRequest, "invalid_adjustment");
    }

    [Fact]
    public async Task Adjust_UnknownSource_Returns400()
    {
        var user = await factory.CreateUserAsync();
        var spool = await AddSpoolAsync(user);

        await AssertProblemAsync(await _client.SendAsync(Adjust(user, spool.Id, new { source = "pounds", delta = 5 }), Ct), HttpStatusCode.BadRequest, "invalid_adjustment");
    }

    [Fact]
    public async Task Adjust_NotesTooLong_Returns400()
    {
        var user = await factory.CreateUserAsync();
        var spool = await AddSpoolAsync(user, usedMg: 400_000);

        await AssertProblemAsync(
            await _client.SendAsync(Adjust(user, spool.Id, new { source = "weight", delta = 5, notes = new string('x', 1001) }), Ct),
            HttpStatusCode.BadRequest, "invalid_adjustment");
    }

    [Fact]
    public async Task Adjust_ForeignSpool_Returns404WithMaterialNotFoundCode()
    {
        var user = await factory.CreateUserAsync();
        var other = await factory.CreateUserAsync();
        var spool = await AddSpoolAsync(user, owner: other, usedMg: 400_000);

        var response = await _client.SendAsync(Adjust(user, spool.Id, new { source = "weight", delta = -5 }), Ct);

        // The Home Assistant client reads an EMPTY 404 as "this API has no such route", so a
        // missing or foreign spool must carry a problem-details body.
        await AssertProblemAsync(response, HttpStatusCode.NotFound, "material_not_found");
    }

    [Fact]
    public async Task Adjust_WithApiKey_Succeeds()
    {
        var user = await factory.CreateUserAsync();
        var spool = await AddSpoolAsync(user, usedMg: 400_000);
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys") { Content = JsonContent.Create(new { Description = "HA" }) };
        create.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        var created = await _client.SendAsync(create, Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var key = (await JsonAsync(created)).GetProperty("publicKey").GetString()!;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Filaments/{spool.Id}/adjustments")
        {
            Content = JsonContent.Create(new { source = "weight", delta = -50 }),
        };
        request.Headers.Add("X-Api-Key", key);
        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(550, (await JsonAsync(response)).GetProperty("afterGrams").GetDouble());
    }

    [Fact]
    public async Task SetActive_False_Returns204_AndSnapshotShowsInactive()
    {
        var user = await factory.CreateUserAsync();
        var spool = await AddSpoolAsync(user);

        var response = await _client.SendAsync(SetActive(user, spool.Id, false), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var snapshot = await JsonAsync(await SnapshotAsync(user));
        Assert.False(SnapshotSpool(snapshot, spool.Id).GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task SetActive_ForeignSpool_Returns404WithMaterialNotFoundCode()
    {
        var user = await factory.CreateUserAsync();
        var other = await factory.CreateUserAsync();
        var spool = await AddSpoolAsync(user, owner: other);

        await AssertProblemAsync(await _client.SendAsync(SetActive(user, spool.Id, false), Ct), HttpStatusCode.NotFound, "material_not_found");
    }
}
