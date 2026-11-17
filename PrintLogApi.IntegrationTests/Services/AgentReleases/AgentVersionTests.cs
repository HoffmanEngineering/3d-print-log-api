using PrintLogApi.Services.AgentReleases;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services.AgentReleases;

public class AgentVersionTests
{
    [Theory]
    [InlineData("0.1.0", "0.2.0", true)]
    [InlineData("0.9.0", "0.10.0", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.3.0", "0.2.0", false)] // edge, ahead of the last release
    [InlineData("v0.1.0", "0.2.0", true)]
    [InlineData("0.2.0.dev1", "0.2.0", false)] // only the first three numbers count
    [InlineData("1.0", "2.0.0", false)]
    [InlineData("unknown", "0.2.0", false)]
    [InlineData(null, "0.2.0", false)]
    [InlineData("0.1.0", null, false)]
    public void IsOlder(string? current, string? latest, bool expected)
        => Assert.Equal(expected, AgentVersion.IsOlder(current, latest));
}
