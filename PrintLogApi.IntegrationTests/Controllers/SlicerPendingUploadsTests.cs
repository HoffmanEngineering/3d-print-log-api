using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.CuraSettings;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// The OrcaSlicer plugin's browser fallback: an upload may carry the plugin's <c>installId</c>,
/// and <c>GET /api/Slicer/uploads/pending</c> lists that install's recent uploads that nobody has
/// claimed yet, for a signed-in user whose browser did not open the new-print page by itself.
/// </summary>
public class SlicerPendingUploadsTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private const string UploadUrl = "/api/Slicer/uploads";
    private const string PendingUrl = "/api/Slicer/uploads/pending";

    private readonly HttpClient _httpClient = factory.CreateClient();

    #region The upload's installId

    [Fact]
    public async Task Upload_WithAnInstallId_StoresIt()
    {
        var installId = NewInstallId();

        var id = await UploadAsync(installId);

        Assert.Equal(installId, (await LoadAsync(id)).InstallId);
    }

    [Fact]
    public async Task Upload_WithoutAnInstallId_StoresNone()
    {
        var id = await UploadAsync(installId: null);

        Assert.Null((await LoadAsync(id)).InstallId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcde")] // 31
    [InlineData("0123456789abcdef0123456789abcdef0")] // 33
    [InlineData("0123456789ABCDEF0123456789ABCDEF")] // uppercase
    [InlineData("0123456789abcdef0123456789abcdeg")] // not hex
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")] // a dashed uuid
    [InlineData(" 0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcde\u0663")] // a non-ASCII digit
    public async Task Upload_WithAMalformedInstallId_Returns400(string installId)
    {
        var response = await SendUploadAsync(Metadata(installId));

        await AssertStatusAsync(HttpStatusCode.BadRequest, response);
    }

    [Fact]
    public async Task Upload_WithANonStringInstallId_Returns400()
    {
        var response = await SendUploadAsync("""{ "pluginVersion": "0.1.0", "template": "default", "installId": 12345 }""");

        await AssertStatusAsync(HttpStatusCode.BadRequest, response);
    }

    #endregion

    #region GET /api/Slicer/uploads/pending

    [Fact]
    public async Task Pending_ListsAnUpload_WithItsSummaryFromTheStoredSettings()
    {
        var installId = NewInstallId();
        var id = await UploadAsync(installId, outputName: @"C:\Prints\calibration_cube_final.gcode");
        var stored = await LoadAsync(id);

        var items = await GetPendingAsync(installId);

        var item = Assert.Single(items);
        Assert.Equal(id, item.GetProperty("settingId").GetGuid());
        Assert.Equal("Calibration Cube Final", item.GetProperty("printName").GetString());
        Assert.Equal("calibration_cube_final.gcode", item.GetProperty("fileName").GetString());
        Assert.Equal(stored.Settings.GetProperty("estimated_print_time_seconds").GetDouble(), item.GetProperty("estimatedPrintTimeSeconds").GetDouble());
        Assert.True(item.GetProperty("estimatedPrintTimeSeconds").GetInt64() > 0);
        Assert.Equal("2.2.0-rc", item.GetProperty("slicerVersion").GetString());
        Assert.Equal("orca-plugin/0.1.0", item.GetProperty("pluginVersion").GetString());
        Assert.False(item.GetProperty("claimed").GetBoolean());
        Assert.Equal(stored.CreatedDate.ToUnixTimeSeconds(), item.GetProperty("createdDate").GetDateTimeOffset().ToUnixTimeSeconds());

        // Exactly the documented fields: never the snapshot, the note or the raw settings.
        Assert.Equal(
            new[] { "claimed", "createdDate", "estimatedPrintTimeSeconds", "fileName", "pluginVersion", "printName", "settingId", "slicerVersion" },
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Pending_ListsOnlyThatInstallsUnclaimedUploadsFromTheLastSevenDays_NewestFirst()
    {
        var installId = NewInstallId();
        var now = DateTimeOffset.UtcNow;
        var newest = await SeedAsync(installId, now.AddMinutes(-1));
        var middle = await SeedAsync(installId, now.AddDays(-3));
        var oldest = await SeedAsync(installId, now.AddDays(-7).AddMinutes(5));
        await SeedAsync(installId, now.AddDays(-7).AddMinutes(-5)); // past the window
        await SeedAsync(installId, now.AddDays(-30));
        await SeedAsync(NewInstallId(), now); // another install
        await SeedAsync(installId: null, now); // no install

        var items = await GetPendingAsync(installId);

        Assert.Equal([newest, middle, oldest], items.Select(i => i.GetProperty("settingId").GetGuid()));
    }

    [Fact]
    public async Task Pending_ReturnsAtMostTwenty_TheNewest()
    {
        var installId = NewInstallId();
        var now = DateTimeOffset.UtcNow;
        var ids = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            ids.Add(await SeedAsync(installId, now.AddMinutes(-i)));
        }

        var items = await GetPendingAsync(installId);

        Assert.Equal(ids.Take(20), items.Select(i => i.GetProperty("settingId").GetGuid()));
    }

    [Fact]
    public async Task Pending_LeavesOutUploadsAnotherUserClaimed()
    {
        var installId = NewInstallId();
        var mine = await SeedAsync(installId, DateTimeOffset.UtcNow.AddMinutes(-2));
        await SeedAsync(installId, DateTimeOffset.UtcNow.AddMinutes(-1), userId: IntegrationTestSeeder.SecondaryUserId);

        var items = await GetPendingAsync(installId);

        Assert.Equal([mine], items.Select(i => i.GetProperty("settingId").GetGuid()));
    }

    [Fact]
    public async Task Pending_IncludesTheCallersOwnClaimedUpload_MarkedClaimed()
    {
        var installId = NewInstallId();
        var open = await SeedAsync(installId, DateTimeOffset.UtcNow.AddMinutes(-2));
        var opened = await SeedAsync(installId, DateTimeOffset.UtcNow.AddMinutes(-1), userId: IntegrationTestSeeder.TestUserId);

        var items = await GetPendingAsync(installId);

        Assert.Equal([opened, open], items.Select(i => i.GetProperty("settingId").GetGuid()));
        Assert.Equal([true, false], items.Select(i => i.GetProperty("claimed").GetBoolean()));
    }

    [Fact]
    public async Task Pending_AfterTheBrowserClaimsAnUpload_ShowsItClaimed()
    {
        var installId = NewInstallId();
        var id = await UploadAsync(installId);

        var claim = new HttpRequestMessage(HttpMethod.Get, $"/api/Cura/settings?id={id}");
        claim.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var claimed = await _httpClient.SendAsync(claim, TestContext.Current.CancellationToken);
        await AssertStatusAsync(HttpStatusCode.OK, claimed);
        using (var body = JsonDocument.Parse(await claimed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            // The install id stays server-side: the plugins' shape of this response is unchanged.
            Assert.False(body.RootElement.TryGetProperty("installId", out _));
        }

        var item = Assert.Single(await GetPendingAsync(installId));
        Assert.True(item.GetProperty("claimed").GetBoolean());

        // And to another user it is gone.
        Assert.Empty(await GetPendingAsync(installId, IntegrationTestSeeder.SecondaryUserOAuthId));
    }

    [Fact]
    public async Task Pending_ClaimsNothing_AndLeavesTheStoredSettingsAsTheyWere()
    {
        var installId = NewInstallId();
        var id = await UploadAsync(installId);
        var before = await LoadAsync(id);

        Assert.Single(await GetPendingAsync(installId));
        Assert.Single(await GetPendingAsync(installId, IntegrationTestSeeder.SecondaryUserOAuthId));

        var after = await LoadAsync(id);
        Assert.Null(after.UserId);
        Assert.Equal(before.CreatedDate, after.CreatedDate);
        Assert.Equal(before.CuraVersion, after.CuraVersion);
        Assert.Equal(before.PluginVersion, after.PluginVersion);
        Assert.Equal(before.Slicer, after.Slicer);
        Assert.Equal(before.InstallId, after.InstallId);
        Assert.True(JsonElement.DeepEquals(before.Settings, after.Settings));
    }

    [Fact]
    public async Task Pending_ReadsWhatItCanFromSettingsWithoutTheSummaryFields()
    {
        var installId = NewInstallId();
        await SeedAsync(installId, DateTimeOffset.UtcNow, settings: """{ "print_name": 7, "file_name": null, "estimated_print_time_seconds": "soon" }""");
        await SeedAsync(installId, DateTimeOffset.UtcNow.AddMinutes(-1), settings: "[]");
        await SeedAsync(installId, DateTimeOffset.UtcNow.AddMinutes(-2), settings: """{ "estimated_print_time_seconds": 3600.6 }""");

        var items = await GetPendingAsync(installId);

        Assert.Equal(3, items.Count);
        Assert.All(items, i =>
        {
            Assert.Equal(JsonValueKind.Null, i.GetProperty("printName").ValueKind);
            Assert.Equal(JsonValueKind.Null, i.GetProperty("fileName").ValueKind);
        });
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("estimatedPrintTimeSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("estimatedPrintTimeSeconds").ValueKind);
        Assert.Equal(3601, items[2].GetProperty("estimatedPrintTimeSeconds").GetInt64());
    }

    [Fact]
    public async Task Pending_ForAnInstallWithNothing_ReturnsAnEmptyList()
    {
        Assert.Empty(await GetPendingAsync(NewInstallId()));
    }

    [Fact]
    public async Task Pending_WithoutSignIn_Returns401()
    {
        var installId = NewInstallId();
        await SeedAsync(installId, DateTimeOffset.UtcNow);

        var response = await _httpClient.GetAsync($"{PendingUrl}?installId={installId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Pending_WithAnApiKey_Returns401()
    {
        var installId = NewInstallId();
        await SeedAsync(installId, DateTimeOffset.UtcNow);
        string apiKey;
        using (var scope = factory.Services.CreateScope())
        {
            var keys = scope.ServiceProvider.GetRequiredService<IUserApiKeyService>();
            apiKey = (await keys.GenerateNewApiKey(IntegrationTestSeeder.TestUserId, "slicer-pending-test")).PublicKey!;
        }

        var request = new HttpRequestMessage(HttpMethod.Get, $"{PendingUrl}?installId={installId}");
        request.Headers.Add("X-Api-Key", apiKey);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // InteractiveUserOnly: the request arrives anonymous and is challenged, as for /api/Devices.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?installId=")]
    [InlineData("?installId=0123456789abcdef0123456789abcde")]
    [InlineData("?installId=0123456789abcdef0123456789abcdef0")]
    [InlineData("?installId=0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("?installId=0123456789abcdef0123456789abcdeg")]
    [InlineData("?installId=01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("?installId=%200123456789abcdef0123456789abcdef")]
    public async Task Pending_WithAMalformedInstallId_Returns400(string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, PendingUrl + query);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertStatusAsync(HttpStatusCode.BadRequest, response);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    #endregion

    #region Helpers

    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "tests-fixtures");

    private static string NewInstallId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static string Metadata(string? installId, string outputName = "calibration_cube.gcode") =>
        JsonSerializer.Serialize(new
        {
            pluginVersion = "0.1.0",
            slicerVersion = "2.2.0-rc",
            outputName,
            host = "File",
            template = "default",
            installId,
        });

    private async Task<Guid> UploadAsync(string? installId, string outputName = "calibration_cube.gcode")
    {
        var response = await SendUploadAsync(Metadata(installId, outputName));
        await AssertStatusAsync(HttpStatusCode.OK, response);
        return (await response.Content.ReadFromJsonAsync<NewCuraSettingsDto>(TestContext.Current.CancellationToken))!.NewSettingId;
    }

    private async Task<HttpResponseMessage> SendUploadAsync(string metadata)
    {
        var text = System.IO.File.ReadAllText(
            Path.Combine(FixtureDirectory, "slicer-uploads", "orcaslicer-2.2.0-rc-calibration-cube.gcode"), Encoding.UTF8);
        using var gzipped = new MemoryStream();
        using (var gzip = new GZipStream(gzipped, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        var part = new ByteArrayContent(gzipped.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        using var content = new MultipartFormDataContent
        {
            { part, "gcode", "upload.gcode.gz" },
            { new StringContent(metadata, Encoding.UTF8, "application/json"), "metadata" },
        };

        return await _httpClient.PostAsync(UploadUrl, content, TestContext.Current.CancellationToken);
    }

    /// <summary>A stored upload, as the plugin would have left it (or a user claimed it).</summary>
    private async Task<Guid> SeedAsync(string? installId, DateTimeOffset createdDate, long? userId = null, string? settings = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        using var json = JsonDocument.Parse(settings ?? """{ "print_name": "Seeded", "file_name": "seeded.gcode", "estimated_print_time_seconds": 60, "note": "n", "snapshot": "s" }""");
        var record = new CuraSetting
        {
            Slicer = "OrcaSlicer",
            CuraVersion = "2.2.0",
            PluginVersion = "orca-plugin/0.1.0",
            Settings = json.RootElement.Clone(),
            CreatedDate = createdDate,
            UserId = userId,
            InstallId = installId,
        };
        context.CuraSettings.Add(record);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return record.Id;
    }

    private async Task<List<JsonElement>> GetPendingAsync(string installId, string user = IntegrationTestSeeder.TestUserOAuthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{PendingUrl}?installId={installId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        await AssertStatusAsync(HttpStatusCode.OK, response);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    private async Task<CuraSetting> LoadAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await context.CuraSettings.AsNoTracking().SingleAsync(c => c.Id == id, TestContext.Current.CancellationToken);
    }

    private static async Task AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Fail($"Expected {expected}, got {response.StatusCode}: {body}");
        }
    }

    #endregion
}
