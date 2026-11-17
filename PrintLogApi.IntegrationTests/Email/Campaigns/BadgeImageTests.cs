using System.Text.RegularExpressions;
using PrintLogApi.Achievements;
using PrintLogApi.Email.Campaigns;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email.Campaigns;

// Mirrors the UI's badgeFillId and email-assets badgeFileName: the API names the file, the UI
// generator produced it, and neither CI can see the other side.
public class BadgeImageTests
{
    private static AchievementDefinition Def(string key) => AchievementCatalog.Find(key)!;

    [Fact]
    public void Numeral_CarriesTheTierThreshold()
        => Assert.Equal("numeral-100-t4.png", BadgeImage.FileName(Def("prints-logged"), 4));

    [Fact]
    public void Tiered_UsesTheTierFinish()
        => Assert.Equal("clock-t2.png", BadgeImage.FileName(Def("print-hours"), 2));

    [Fact]
    public void OneTime_UsesTheCategoryColor()
        => Assert.Equal("robot-in.png", BadgeImage.FileName(Def("mcp"), 1));

    [Fact]
    public void Initials_AreLowercasedAndHyphenated()
        => Assert.Equal("initials-cu-in.png", BadgeImage.FileName(Def("slicer-cura"), 1));

    [Fact]
    public void Hidden_UsesTheHiddenColor()
        => Assert.Equal("noodles-hi.png", BadgeImage.FileName(Def("spaghetti"), 1));

    [Fact]
    public void GettingStarted_OneTime_UsesItsCategoryColor()
        => Assert.Equal("layers-gs.png", BadgeImage.FileName(Def("first-print"), 1));

    [Fact]
    public void EveryCatalogTier_HasAWellFormedFileName()
    {
        var pattern = new Regex(@"^[a-z0-9-]+-(t[1-6]|gs|in|co|hi)\.png$");
        foreach (var def in AchievementCatalog.Definitions)
        {
            for (var tier = 1; tier <= def.Thresholds.Count; tier++)
            {
                Assert.Matches(pattern, BadgeImage.FileName(def, tier));
            }
        }
    }
}
