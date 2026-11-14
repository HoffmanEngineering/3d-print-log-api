using PrintLogApi.SpoolTags.Labels;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

public class LabelTextMatcherTests
{
    private static readonly string[] Catalog = ["ELEGOO", "Elegoo Rapid", "OVERTURE", "Polymaker", "PLA Pro Co", "SUNLU", "Bambu Lab"];

    [Theory]
    [InlineData("ELEGOO PLA 1.75mm 1kg Printing Temp:190°C-230°C", "ELEGOO")]
    [InlineData("FEATURES OVERTURE\"... — Silk Shining Surface", "OVERTURE")]
    [InlineData("Fiberon by POLYMAKFR PA612-CF15", "Polymaker")]
    [InlineData("made by bambu lab", "Bambu Lab")]
    public void FindBrand_FindsCatalogBrands(string text, string expected)
    {
        Assert.Equal(expected, LabelTextMatcher.FindBrand(text, [], Catalog));
    }

    [Fact]
    public void FindBrand_ShortTokensMustMatchExactly()
    {
        // "PLA" alone is a material, never the brand "PLA Pro Co"; a 4-letter word needs no typo.
        Assert.Null(LabelTextMatcher.FindBrand("PLA 1.75mm Black", [], Catalog));
        Assert.Null(LabelTextMatcher.FindBrand("ESUM PLA", [], ["eSUN"]));
        Assert.Equal("eSUN", LabelTextMatcher.FindBrand("ESUN PLA", [], ["eSUN"]));
    }

    [Fact]
    public void FindBrand_PrefersTheLongestName()
    {
        Assert.Equal("Elegoo Rapid", LabelTextMatcher.FindBrand("ELEGOO RAPID PLA+", [], Catalog));
    }

    [Fact]
    public void FindBrand_TheUsersOwnBrandWinsATie()
    {
        Assert.Equal("Overture3D", LabelTextMatcher.FindBrand("OVERTURE3D PETG", ["Overture3D"], ["OVERTURE3D"]));
    }

    [Fact]
    public void FindBrand_NothingFound_IsNull()
    {
        Assert.Null(LabelTextMatcher.FindBrand("Print Temp 210-220 Bed 50-60", [], Catalog));
    }

    [Fact]
    public void FindColor_PicksTheLongestName()
    {
        Assert.Equal("Sea Green", LabelTextMatcher.FindColor("ELEGOO PLA colour:Sea Green 1kg", ["Green", "Sea Green", "Black"]));
    }

    [Fact]
    public void FindColor_IgnoresNamesNotOnTheLabel()
    {
        Assert.Null(LabelTextMatcher.FindColor("PLA Marble 1.75mm", ["Black", "White"]));
    }

    [Fact]
    public void NonAsciiText_DoesNotThrow()
    {
        Assert.Null(LabelTextMatcher.FindBrand("打印温度 200±10℃ 热床", [], Catalog));
        Assert.Null(LabelTextMatcher.FindColor("打印温度 200±10℃", ["Black"]));
    }
}
