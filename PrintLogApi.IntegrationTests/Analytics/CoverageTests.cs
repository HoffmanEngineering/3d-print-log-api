using PrintLogApi.Models.DTOs.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics;

public class CoverageTests
{
    [Fact]
    public void Builder_AggregatesRepeatedReasonsIntoOneCountedEntry()
    {
        var b = new CoverageBuilder("prints") { Total = 10, Counted = 7 };
        b.Exclude(ExclusionReason.PriceMissing);
        b.Exclude(ExclusionReason.PriceMissing, 2);
        b.Exclude(ExclusionReason.CurrencyMismatch);

        var coverage = b.Build();

        Assert.Equal(3, coverage.Exclusions.Single(e => e.Reason == ExclusionReason.PriceMissing).Count);
        Assert.Equal(1, coverage.Exclusions.Single(e => e.Reason == ExclusionReason.CurrencyMismatch).Count);
    }

    [Fact]
    public void Builder_OmitsZeroCountReasons()
    {
        var b = new CoverageBuilder("prints") { Total = 3, Counted = 3 };
        b.Exclude(ExclusionReason.Undated, 0);

        Assert.Empty(b.Build().Exclusions);
    }

    [Fact]
    public void Empty_IsAZeroCoverageForThatPopulation()
    {
        var c = Coverage.Empty("spools");
        Assert.Equal("spools", c.Population);
        Assert.Equal(0, c.Total);
        Assert.Empty(c.Exclusions);
    }
}
