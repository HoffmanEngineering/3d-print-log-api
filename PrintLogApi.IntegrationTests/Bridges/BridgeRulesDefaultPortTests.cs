using PrintLogApi.Services.Bridges;
using Xunit;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>The port each adapter uses when a printer entry names none (<see cref="BridgeRules.DefaultPort"/>).</summary>
public class BridgeRulesDefaultPortTests
{
    [Theory]
    [InlineData("moonraker", 7125)]
    [InlineData("bambu", 8883)]
    [InlineData("prusalink", 80)]
    [InlineData("octoprint", 80)]
    [InlineData("sdcp", 3030)]
    [InlineData("duet", 80)]
    [InlineData("flashforge", 8898)]
    [InlineData("anycubic", 18910)]
    [InlineData("something-else", 0)]
    [InlineData(null, 0)]
    public void EachKind_HasItsAdaptersPort(string? kind, int port)
        => Assert.Equal(port, BridgeRules.DefaultPort(kind));

    [Fact]
    public void AMissingPort_IsTheKindsDefault()
    {
        Assert.True(BridgeRules.SamePort("flashforge", null, 8898));
        Assert.False(BridgeRules.SamePort("flashforge", null, 80));
    }
}
