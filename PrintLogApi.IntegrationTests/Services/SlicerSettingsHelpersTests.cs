using System.Diagnostics;
using System.Text;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services.SlicerSettings;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The text handling behind connector slicer settings (#164): what is scrubbed before parsing, and
/// how the settings section finds its place in the user's notes.
/// </summary>
public class SlicerSettingsHelpersTests
{
    #region Scrubbing

    [Theory]
    [InlineData("print_host")]
    [InlineData("printhost_apikey")]
    [InlineData("printhost_password")]
    [InlineData("printhost_user")]
    [InlineData("PrintHost_CAFile")]
    [InlineData("bbl_use_printhost")]
    [InlineData("octoprint_api_key")]
    [InlineData("some_token")]
    [InlineData("cloud_secret")]
    [InlineData("post_process")]
    [InlineData("access_code")]
    public void SensitiveKeys_AreRecognized(string key) =>
        Assert.True(SlicerSettingsScrubber.IsSensitiveKey(key));

    [Theory]
    [InlineData("layer_height")]
    [InlineData("host_type")]
    [InlineData("slowdown_for_curled_perimeters")]
    [InlineData("print_settings_id")]
    [InlineData("filament_settings_id")]
    public void OrdinarySettings_AreKept(string key) =>
        Assert.False(SlicerSettingsScrubber.IsSensitiveKey(key));

    [Fact]
    public void ScrubText_BlanksOnlySensitiveValues_AndKeepsEveryOtherByte()
    {
        var text = "; layer_height = 0.2\r\n;printhost_apikey=abc123\r\n; print_host: http://pi.local\n" +
                   "G1 X1 ; printhost_password = not-a-setting-line\n; password\n;  printhost_user  =  me  ";

        var scrubbed = SlicerSettingsScrubber.ScrubText(text);

        Assert.Equal(
            "; layer_height = 0.2\r\n;printhost_apikey=\r\n; print_host:\n" +
            "G1 X1 ; printhost_password = not-a-setting-line\n; password\n;  printhost_user  =",
            scrubbed);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\f")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void ScrubText_SplitsLinesLikeTheParser(string terminator)
    {
        // The parser library reads lines with EnumerateLines(), which also ends a line at each of
        // these. Splitting only on \n would leave a CR-only block in one "line" and unscrubbed.
        var text = string.Join(terminator, "; layer_height = 0.2", "; printhost_apikey = secret", "; print_host = http://secret", "; nozzle_diameter = 0.4");

        var scrubbed = SlicerSettingsScrubber.ScrubText(text);

        Assert.Equal(
            string.Join(terminator, "; layer_height = 0.2", "; printhost_apikey =", "; print_host =", "; nozzle_diameter = 0.4"),
            scrubbed);
    }

    [Fact]
    public void ScrubText_NothingSensitive_ReturnsTheSameText()
    {
        const string text = "; layer_height = 0.2\n; host_type = octoprint\n";

        Assert.Same(text, SlicerSettingsScrubber.ScrubText(text));
    }

    [Fact]
    public void ScrubText_IsLinear()
    {
        // The whole block is attacker-controlled; a cap-sized block of nothing but sensitive lines
        // must not take noticeably longer than a normal one.
        var text = new StringBuilder().Insert(0, "; printhost_apikey = secret\n", PrintSlicerSettingsDto.MaxConfigBlockBytes / 28).ToString();

        var stopwatch = Stopwatch.StartNew();
        var scrubbed = SlicerSettingsScrubber.ScrubText(text);
        stopwatch.Stop();

        Assert.DoesNotContain("secret", scrubbed);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"{stopwatch.ElapsedMilliseconds} ms");
    }

    #endregion

    #region The notes section

    private static readonly string Section = SlicerSettingsNote.Section("OrcaSlicer", "2.3.1", "Layer Height: 0.2");

    [Fact]
    public void Section_HasAHeadingTheNoteAndAnEndLine()
    {
        Assert.Equal("--- Slicer settings (OrcaSlicer 2.3.1) ---\nLayer Height: 0.2\n--- End of slicer settings ---", Section);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Mine.", "Mine.\n\n")]
    [InlineData("Mine.\n", "Mine.\n\n")]
    [InlineData("Mine.\n\n", "Mine.\n\n")]
    public void Upsert_WithNoSection_AppendsAfterABlankLine(string? notes, string expectedPrefix)
    {
        Assert.Equal(expectedPrefix + Section, SlicerSettingsNote.Upsert(notes, Section));
    }

    [Fact]
    public void Upsert_ReplacesTheSection_InCrlfNotesToo()
    {
        var notes = "Before.\r\n\r\n--- Slicer settings (PrusaSlicer 2.9.2) ---\r\nOld\r\n--- End of slicer settings ---\r\n\r\nAfter.";

        var updated = SlicerSettingsNote.Upsert(notes, Section);

        Assert.Equal("Before.\r\n\r\n" + Section + "\r\n\r\nAfter.", updated);
    }

    [Fact]
    public void Upsert_WhenTheUserRemovedTheEndLine_AppendsRatherThanDeletingTheirText()
    {
        var notes = "--- Slicer settings (PrusaSlicer 2.9.2) ---\nOld\nThe user's own paragraph.";

        var updated = SlicerSettingsNote.Upsert(notes, Section);

        Assert.StartsWith(notes, updated);
        Assert.EndsWith(Section, updated);
    }

    [Fact]
    public void Upsert_AfterAnOrphanedHeading_NeverReplacesTheUsersTextBelowIt()
    {
        // The user deleted the end line but kept the old section and wrote below it. The second
        // send appends a fresh section; the third must replace only that one, not pair the
        // orphaned heading with the new end line and take the user's paragraph with it.
        var orphaned = "Mine.\n\n--- Slicer settings (PrusaSlicer 2.9.2) ---\nOld\n\nThe user's own paragraph.";
        var second = SlicerSettingsNote.Upsert(orphaned, Section);
        Assert.Equal(orphaned + "\n\n" + Section, second);

        var third = SlicerSettingsNote.Section("OrcaSlicer", "2.3.2", "Layer Height: 0.28");
        var updated = SlicerSettingsNote.Upsert(second, third);

        Assert.Equal(orphaned + "\n\n" + third, updated);
    }

    [Fact]
    public void Render_KeepsFreeTextOnOneLine_SoItCannotForgeTheEndLine()
    {
        var note = SlicerSettingsNote.Render(new SlicerMetadataDto
        {
            FilamentName = "PLA\n" + SlicerSettingsNote.EndLine,
        });

        Assert.Equal("Filament: PLA " + SlicerSettingsNote.EndLine, note);
        Assert.DoesNotContain("\n", note);
    }

    #endregion
}
