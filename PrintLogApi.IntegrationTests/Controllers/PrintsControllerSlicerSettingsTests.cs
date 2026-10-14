using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;
using PrintLogApi.Services.SlicerSettings;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Slicer settings from connectors (#164): a connector sends the settings block it read from the
/// G-code, or Moonraker's metadata summary, and the print gets its slicer, version, a settings note
/// and a settings record without changing how it was logged.
/// </summary>
public class PrintsControllerSlicerSettingsTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private const string OrcaCube = "orcaslicer-2.2.0-rc-calibration-cube";
    private const string PrusaCube = "prusaslicer-2.9.2-calibration-cube";
    private const string AnycubicCube = "anycubicslicernext-1.3.2-calibration-cube";

    private readonly HttpClient _httpClient = factory.CreateClient();

    #region The note matches the uploader

    // tests-fixtures/slicer-settings holds three of the parser library's own G-code fixtures
    // (PrintLog.SlicerSettings.Tests/TestData in the Slic3rPostProcessingUploader repo), cut the
    // way that library's ConfigBlockFixture.Split cuts them: the header, and the settings block a
    // connector would send. Each *.dry-run-note.txt is settings.note from the uploader's own
    // `--dry-run --debug` output for the whole, uncut file. Snapmaker Orca has no fixture in the
    // library, so it is not covered here; its G-code reads as OrcaSlicer.
    [Theory]
    [InlineData(PrusaCube, "prusaslicer", "2.9.2")]
    [InlineData(OrcaCube, "orcaslicer", "2.2.0-rc")]
    [InlineData(AnycubicCube, "anycubic", "1.3.2")]
    public async Task ConfigBlock_WithHeader_RendersTheUploadersNote(string fixture, string slicer, string version)
    {
        var print = await CreatePrintAsync();

        var result = await ReadResultAsync(await SendAsync(print.Id, FromFixture(fixture)));

        Assert.Equal(slicer, result.Slicer);
        Assert.Equal(version, result.SlicerVersion);
        Assert.True(result.IsRecognized);
        Assert.Equal(ExpectedNote(fixture), SettingsSection((await LoadPrintAsync(print.Id)).Notes));
    }

    [Fact]
    public async Task ConfigBlock_WithoutHeader_StillParsesButTheVersionComesFromMetadata()
    {
        var print = await CreatePrintAsync();
        var body = FromFixture(PrusaCube);
        body.Header = null;
        body.Metadata = new SlicerMetadataDto { SlicerVersion = "2.9.2" };

        var result = await ReadResultAsync(await SendAsync(print.Id, body));

        Assert.Equal("prusaslicer", result.Slicer);
        Assert.Equal("2.9.2", result.SlicerVersion);
        Assert.False(result.IsRecognized);
    }

    [Fact]
    public async Task ConfigBlock_WithoutHeaderOrVersion_LeavesTheVersionUnset()
    {
        var print = await CreatePrintAsync();
        var body = FromFixture(PrusaCube);
        body.Header = null;

        var result = await ReadResultAsync(await SendAsync(print.Id, body));

        // The library reports "Unknown"; that is not a version.
        Assert.Null(result.SlicerVersion);
        Assert.Null((await LoadPrintAsync(print.Id)).SlicerVersion);
    }

    #endregion

    #region Provenance

    [Fact]
    public async Task Send_SetsSlicerAndVersion_AndKeepsSourceAndExternalSource()
    {
        var created = await CreatePrintAsync(externalSource: "moonraker", externalId: UniqueExternalId());
        var before = await LoadPrintAsync(created.Id);

        await ReadResultAsync(await SendAsync(created.Id, FromFixture(OrcaCube)));

        var after = await LoadPrintAsync(created.Id);
        Assert.Equal("orcaslicer", after.Slicer);
        Assert.Equal("2.2.0-rc", after.SlicerVersion);
        Assert.Equal(before.Source, after.Source);
        Assert.Equal("moonraker", after.ExternalSource);
        Assert.Equal(before.ExternalId, after.ExternalId);
    }

    [Fact]
    public async Task Send_StoresOneSettingsRecordLinkedToThePrint()
    {
        var print = await CreatePrintAsync();

        await ReadResultAsync(await SendAsync(print.Id, FromFixture(OrcaCube)));

        var record = Assert.Single(await LoadSettingsAsync(print.Id));
        Assert.Equal("OrcaSlicer", record.Slicer);
        Assert.Equal("2.2.0-rc", record.CuraVersion);
        Assert.Equal(IntegrationTestSeeder.TestUserId, record.UserId);
        var stored = record.Settings;
        Assert.Equal(ExpectedNote(OrcaCube), stored.GetProperty("note").GetString());
        Assert.Equal("0.2", stored.GetProperty("settings").GetProperty("layer_height").GetString());
        Assert.Equal("configBlock", stored.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Send_MakesThePrintReportItsSettingsToClaimingConnectors()
    {
        var print = await CreatePrintAsync();

        await ReadResultAsync(await SendAsync(print.Id, FromFixture(OrcaCube)));

        var claimable = await GetAsync<List<ClaimablePrintDto>>($"/api/Prints/claimable?printerId={IntegrationTestSeeder.TestPrinterId}&limit=100");
        Assert.True(Assert.Single(claimable, p => p.Id == print.Id).HasSlicerSettings);
    }

    [Fact]
    public async Task Send_InvalidatesTheOwnersCachedSummaries()
    {
        var print = await CreatePrintAsync();
        var versions = factory.Services.GetRequiredService<ICacheVersionService>();
        var before = versions.GetUserCacheVersion(IntegrationTestSeeder.TestUserId);

        await ReadResultAsync(await SendAsync(print.Id, FromFixture(OrcaCube)));

        Assert.NotEqual(before, versions.GetUserCacheVersion(IntegrationTestSeeder.TestUserId));
    }

    #endregion

    #region Notes

    [Fact]
    public async Task Send_AppendsUnderAHeading_AndKeepsTheUsersNotes()
    {
        var print = await CreatePrintAsync(notes: "Printed for the kitchen drawer.");

        await ReadResultAsync(await SendAsync(print.Id, FromFixture(PrusaCube)));

        var notes = (await LoadPrintAsync(print.Id)).Notes!;
        Assert.StartsWith("Printed for the kitchen drawer.\n\n" + SlicerSettingsNote.HeadingPrefix, notes);
        Assert.EndsWith(SlicerSettingsNote.EndLine, notes);
    }

    [Fact]
    public async Task Send_ToAPrintWithNoNotes_IsJustTheSection()
    {
        var print = await CreatePrintAsync();

        await ReadResultAsync(await SendAsync(print.Id, FromFixture(PrusaCube)));

        Assert.StartsWith(SlicerSettingsNote.HeadingPrefix, (await LoadPrintAsync(print.Id)).Notes);
    }

    [Fact]
    public async Task SendTwice_LeavesOneNoteAndOneRecord()
    {
        var print = await CreatePrintAsync(notes: "Mine.");

        await ReadResultAsync(await SendAsync(print.Id, FromFixture(PrusaCube)));
        var retry = await ReadResultAsync(await SendAsync(print.Id, FromFixture(PrusaCube)));

        Assert.True(retry.Replaced);
        var notes = (await LoadPrintAsync(print.Id)).Notes!;
        Assert.Equal(1, Occurrences(notes, SlicerSettingsNote.HeadingPrefix));
        Assert.Equal(1, Occurrences(notes, SlicerSettingsNote.EndLine));
        Assert.StartsWith("Mine.\n\n", notes);
        Assert.Single(await LoadSettingsAsync(print.Id));
    }

    [Fact]
    public async Task SendAgain_WithDifferentSettings_ReplacesTheEarlierNote_AndKeepsTextAfterIt()
    {
        var print = await CreatePrintAsync(notes: "Before.");
        await ReadResultAsync(await SendAsync(print.Id, FromFixture(PrusaCube)));
        // The user adds a line below the section in the web app.
        await PatchNotesAsync(print.Id, (await LoadPrintAsync(print.Id)).Notes + "\n\nAfter.");

        await ReadResultAsync(await SendAsync(print.Id, FromFixture(OrcaCube)));

        var notes = (await LoadPrintAsync(print.Id)).Notes!;
        Assert.Equal(1, Occurrences(notes, SlicerSettingsNote.HeadingPrefix));
        Assert.Contains("Printer: JGMaker R1", notes);
        Assert.DoesNotContain("Perimeter Generator: classic", notes);
        Assert.StartsWith("Before.\n\n", notes);
        Assert.EndsWith("\n\nAfter.", notes);
        var record = Assert.Single(await LoadSettingsAsync(print.Id));
        Assert.Equal("OrcaSlicer", record.Slicer);
        Assert.Equal("orcaslicer", (await LoadPrintAsync(print.Id)).Slicer);
    }

    [Fact]
    public async Task Send_WhenTheNotesWouldOutgrowTheEditForm_StoresTheSettingsWithoutTheNote()
    {
        // The web form rejects notes over 50,000 characters, so appending past that would make
        // the print impossible to edit there.
        var userNotes = new string('x', 49_000);
        var print = await CreatePrintAsync(notes: userNotes);

        var result = await ReadResultAsync(await SendAsync(print.Id, FromFixture(OrcaCube)));

        Assert.False(result.NoteAppended);
        var stored = await LoadPrintAsync(print.Id);
        Assert.Equal(userNotes, stored.Notes);
        Assert.Equal("orcaslicer", stored.Slicer);
        Assert.Single(await LoadSettingsAsync(print.Id));
    }

    #endregion

    #region Metadata summary

    [Fact]
    public async Task MetadataOnly_SetsTheSlicerAndWritesASummaryNote()
    {
        var print = await CreatePrintAsync();

        var result = await ReadResultAsync(await SendAsync(print.Id, new PrintSlicerSettingsDto { Metadata = Moonraker() }));

        Assert.Equal("orcaslicer", result.Slicer);
        Assert.Equal("2.3.1", result.SlicerVersion);
        Assert.Null(result.IsRecognized);
        var note = SettingsSection((await LoadPrintAsync(print.Id)).Notes);
        Assert.Contains("Layer Height: 0.2 mm", note);
        Assert.Contains("First Layer Height: 0.24 mm", note);
        Assert.Contains("Nozzle Diameter: 0.4 mm", note);
        Assert.Contains("Filament: Generic PLA (PLA)", note);
        Assert.Contains("First Layer Nozzle Temperature: 215 °C", note);
        Assert.Contains("First Layer Bed Temperature: 60 °C", note);
        var record = Assert.Single(await LoadSettingsAsync(print.Id));
        Assert.Equal("metadata", record.Settings.GetProperty("source").GetString());
        Assert.Equal("0.2", record.Settings.GetProperty("settings").GetProperty("layer_height").GetString());
    }

    [Fact]
    public async Task MetadataOnly_UnknownSlicer_IsOther()
    {
        var print = await CreatePrintAsync();
        var metadata = Moonraker();
        metadata.Slicer = "SuperSlicer";

        var result = await ReadResultAsync(await SendAsync(print.Id, new PrintSlicerSettingsDto { Metadata = metadata }));

        Assert.Equal("other", result.Slicer);
    }

    [Fact]
    public async Task MetadataOnly_WithoutASlicer_IsBadRequest()
    {
        // SlicerNames reads a missing name as the Cura plugin, so it cannot be left to default.
        var print = await CreatePrintAsync();
        var metadata = Moonraker();
        metadata.Slicer = " ";

        var response = await SendAsync(print.Id, new PrintSlicerSettingsDto { Metadata = metadata });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await LoadPrintAsync(print.Id)).Slicer);
    }

    [Fact]
    public async Task UnparseableBlock_WithMetadata_FallsBackToTheMetadata()
    {
        var print = await CreatePrintAsync();

        var result = await ReadResultAsync(await SendAsync(print.Id, new PrintSlicerSettingsDto
        {
            ConfigBlock = "G1 X10 Y10\n; nothing to see here\n",
            Metadata = Moonraker(),
        }));

        Assert.Equal("orcaslicer", result.Slicer);
        Assert.Equal("metadata", result.Source);
    }

    #endregion

    #region Rejected input

    [Fact]
    public async Task EmptyBody_IsBadRequest()
    {
        var print = await CreatePrintAsync();

        var response = await SendAsync(print.Id, new PrintSlicerSettingsDto());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnparseableBlock_WithoutMetadata_IsBadRequestAndChangesNothing()
    {
        var print = await CreatePrintAsync(notes: "Mine.");

        var response = await SendAsync(print.Id, new PrintSlicerSettingsDto { ConfigBlock = "G1 X10 Y10\n; nothing to see here\n" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var stored = await LoadPrintAsync(print.Id);
        Assert.Equal("Mine.", stored.Notes);
        Assert.Null(stored.Slicer);
        Assert.Empty(await LoadSettingsAsync(print.Id));
    }

    [Fact]
    public async Task OversizedBlock_IsPayloadTooLargeAndChangesNothing()
    {
        var print = await CreatePrintAsync();
        var body = FromFixture(OrcaCube);
        body.ConfigBlock += "\n; padding = " + new string('x', PrintSlicerSettingsDto.MaxConfigBlockBytes);

        var response = await SendAsync(print.Id, body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null((await LoadPrintAsync(print.Id)).Slicer);
        Assert.Empty(await LoadSettingsAsync(print.Id));
    }

    [Fact]
    public async Task OversizedHeader_IsPayloadTooLarge()
    {
        var print = await CreatePrintAsync();
        var body = FromFixture(OrcaCube);
        body.Header += "\n;" + new string('x', PrintSlicerSettingsDto.MaxHeaderBytes);

        var response = await SendAsync(print.Id, body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task AnotherUsersPrint_IsNotFound()
    {
        var print = await CreatePrintAsync();

        var response = await SendAsync(print.Id, FromFixture(OrcaCube), IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null((await LoadPrintAsync(print.Id)).Slicer);
    }

    [Fact]
    public async Task MissingPrint_IsNotFound()
    {
        var response = await SendAsync(long.MaxValue, FromFixture(OrcaCube));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PrintWithSlicerPluginSettings_IsConflictAndKeepsThem()
    {
        // The Cura plugin and the Slic3r uploader read the whole file on the slicer's machine,
        // which is richer than anything a connector sends, so a connector never replaces them.
        var print = await CreatePrintAsync(notes: "Uploader note.");
        await UpdatePrintAsync(print.Id, p =>
        {
            p.Source = PrintSource.SlicerPlugin;
            p.Slicer = "prusaslicer";
            p.SlicerVersion = "2.9.2";
        });

        var response = await SendAsync(print.Id, FromFixture(OrcaCube));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var stored = await LoadPrintAsync(print.Id);
        Assert.Equal("prusaslicer", stored.Slicer);
        Assert.Equal("2.9.2", stored.SlicerVersion);
        Assert.Equal("Uploader note.", stored.Notes);
        Assert.Empty(await LoadSettingsAsync(print.Id));
    }

    #endregion

    #region Scrubbing

    [Fact]
    public async Task CredentialKeys_NeverReachStorageOrTheResponse()
    {
        var print = await CreatePrintAsync();
        var body = FromFixture(OrcaCube);
        var secrets = new[]
        {
            "; print_host = http://SECRET-host.local",
            "; printhost_apikey = SECRET-apikey",
            "; printhost_password = SECRET-password",
            "; printhost_user = SECRET-user",
            "; printhost_cafile = /home/SECRET-cafile.pem",
            "; physical_printer_api_key = SECRET-api-key",
            "; octoprint_token = SECRET-token",
            "; moonraker_secret = SECRET-secret",
            "; post_process = \"/opt/upload.sh\" --key SECRET-post-process",
        };
        // Both before the block's own copies (first non-empty value wins) and inside the header.
        body.ConfigBlock = string.Join("\n", secrets) + "\n" + body.ConfigBlock;
        body.Header += "\n; printhost_apikey: SECRET-header";

        var response = await SendAsync(print.Id, body);
        var responseText = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("SECRET", responseText);
        Assert.DoesNotContain("SECRET", (await LoadPrintAsync(print.Id)).Notes);
        var record = Assert.Single(await LoadSettingsAsync(print.Id));
        Assert.DoesNotContain("SECRET", record.Settings.GetRawText());
        // The keys themselves are not secret; their values are gone.
        Assert.False(record.Settings.GetProperty("settings").TryGetProperty("printhost_apikey", out _));
        // Ordinary settings survive.
        Assert.Equal("0.2", record.Settings.GetProperty("settings").GetProperty("layer_height").GetString());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\u2028")]
    public async Task CredentialKeys_ListedAsChangedFromTheProfile_NeverReachTheNote(string lineBreak)
    {
        // Orca's {{modified_settings}} renders "key = value" for every key in
        // different_settings_to_system, so a host or key the user changed would be quoted in the
        // note. Only the scrub before parsing stops that; dropping keys from the parsed settings
        // afterwards does not reach the note. The parser also breaks lines at "\r" and U+2028.
        var print = await CreatePrintAsync();
        var body = FromFixture(OrcaCube);
        var leading = string.Join(lineBreak,
            "; different_settings_to_system = print_host;printhost_apikey;nozzle_temperature",
            "; print_host = http://SECRET-host.local",
            "; printhost_apikey = SECRET-apikey");
        // First, so these win over the block's own copies (first non-empty value wins).
        body.ConfigBlock = leading + lineBreak + body.ConfigBlock!.Replace("\n", lineBreak);

        var response = await SendAsync(print.Id, body);
        var responseText = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<PrintSlicerSettingsResultDto>(responseText, System.Text.Json.JsonSerializerOptions.Web)!;
        // The section the secrets would have appeared in did render.
        Assert.Contains("Profile Changes:", result.Note);
        Assert.Contains("nozzle_temperature", result.Note);
        Assert.DoesNotContain("SECRET", responseText);
        Assert.DoesNotContain("SECRET", (await LoadPrintAsync(print.Id)).Notes);
        Assert.DoesNotContain("SECRET", Assert.Single(await LoadSettingsAsync(print.Id)).Settings.GetRawText());
    }

    [Fact]
    public async Task CredentialKeys_DoNotChangeTheNote()
    {
        // The default templates name none of these keys, so scrubbing them leaves the note as the
        // uploader renders it.
        var print = await CreatePrintAsync();
        var body = FromFixture(PrusaCube);
        body.ConfigBlock += "\n; printhost_apikey = SECRET-apikey";

        await ReadResultAsync(await SendAsync(print.Id, body));

        Assert.Equal(ExpectedNote(PrusaCube), SettingsSection((await LoadPrintAsync(print.Id)).Notes));
    }

    #endregion

    #region Helpers

    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "tests-fixtures", "slicer-settings");

    private static string Fixture(string name, string kind) =>
        System.IO.File.ReadAllText(Path.Combine(FixtureDirectory, $"{name}.{kind}.txt"), Encoding.UTF8);

    private static PrintSlicerSettingsDto FromFixture(string name) => new()
    {
        ConfigBlock = Fixture(name, "config"),
        Header = Fixture(name, "header"),
    };

    /// <summary>
    /// The uploader's dry-run note, less its <c>Models:</c> section: object sizes are measured from
    /// the toolpaths, which a settings block does not carry. The parser library documents that as
    /// the one section a settings block cannot reproduce.
    /// </summary>
    private static string ExpectedNote(string name)
    {
        var note = Fixture(name, "dry-run-note");
        var models = note.IndexOf("Models:\n", StringComparison.Ordinal);
        if (models < 0)
        {
            return note;
        }

        var end = note.IndexOf("\n\n", models, StringComparison.Ordinal);
        return note[..models] + note[(end + 2)..];
    }

    /// <summary>The text between the section's heading line and its end line.</summary>
    private static string SettingsSection(string? notes)
    {
        Assert.NotNull(notes);
        var start = notes.IndexOf(SlicerSettingsNote.HeadingPrefix, StringComparison.Ordinal);
        Assert.True(start >= 0, notes);
        var bodyStart = notes.IndexOf('\n', start) + 1;
        var end = notes.IndexOf("\n" + SlicerSettingsNote.EndLine, bodyStart, StringComparison.Ordinal);
        Assert.True(end >= 0, notes);
        return notes[bodyStart..end];
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static SlicerMetadataDto Moonraker() => new()
    {
        Slicer = "OrcaSlicer",
        SlicerVersion = "2.3.1",
        LayerHeightMm = 0.2,
        FirstLayerHeightMm = 0.24,
        NozzleDiameterMm = 0.4,
        FilamentName = "Generic PLA",
        FilamentType = "PLA",
        FirstLayerNozzleTemperatureC = 215,
        FirstLayerBedTemperatureC = 60,
    };

    private static string UniqueExternalId() => $"{Guid.NewGuid()}:{Random.Shared.Next(1, 100000)}";

    private async Task<PrintDetailDTO> CreatePrintAsync(string? notes = null, string? externalSource = null, string? externalId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = $"slicer settings {Guid.NewGuid():N}",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Printing,
            ViewStatus = PrintViewStatus.Private,
            Notes = notes,
            ExternalSource = externalSource,
            ExternalId = externalId,
            FilamentUsage = [],
        });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
    }

    private async Task<HttpResponseMessage> SendAsync(long id, PrintSlicerSettingsDto body, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{id}/slicer-settings");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        request.Content = JsonContent.Create(body);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task PatchNotesAsync(long id, string notes)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/Prints/{id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new PatchPrintDto { Notes = notes });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<PrintSlicerSettingsResultDto> ReadResultAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<PrintSlicerSettingsResultDto>(TestContext.Current.CancellationToken))!;
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<T>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Sets what no endpoint writes directly: provenance from a slicer plugin.</summary>
    private async Task UpdatePrintAsync(long id, Action<Print> update)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = await context.Prints.SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
        update(print);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Print> LoadPrintAsync(long id)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await context.Prints.AsNoTracking().SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
    }

    private async Task<List<CuraSetting>> LoadSettingsAsync(long printId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await context.CuraSettings.AsNoTracking().Where(c => c.PrintId == printId).ToListAsync(TestContext.Current.CancellationToken);
    }

    #endregion
}
