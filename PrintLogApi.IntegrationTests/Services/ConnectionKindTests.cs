using PrintLogApi.Models;
using PrintLogApi.Services.AgentReleases;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The connection kinds printlog-bridge registers, one per adapter (printlog-bridge#82), and what
/// each one means for the print's source and the agent update notice.
/// </summary>
public class ConnectionKindTests
{
    public static TheoryData<string> BridgeKinds => ["moonraker", "octoprint", "prusalink", "bambu", "sdcp", "duet", "flashforge", "anycubic"];

    [Theory]
    [MemberData(nameof(BridgeKinds))]
    public void EveryAdapterKind_IsABridgeKind(string kind)
    {
        Assert.True(Connection.IsBridgeKind(kind));
        Assert.True(Connection.IsBridgeKind(kind.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("octoprint-webhook")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherKinds_AreNotBridgeKinds(string? kind)
    {
        Assert.False(Connection.IsBridgeKind(kind));
    }

    [Fact]
    public void BridgeKinds_AreExactlyTheAdapters()
    {
        Assert.Equal(["moonraker", "octoprint", "prusalink", "bambu", "sdcp", "duet", "flashforge", "anycubic"], Connection.BridgeKinds);
    }

    [Theory]
    [MemberData(nameof(BridgeKinds))]
    public void EveryBridgeKind_HasTheBridgeAsItsReleaseRepository(string kind)
    {
        // Without an entry, updateAvailable is never set for that adapter's connections.
        Assert.Equal("HoffmanEngineering/printlog-bridge", new AgentReleaseOptions().Repositories[kind]);
    }

    [Theory]
    [InlineData("octoprint", PrintSource.OctoPrint)]
    [InlineData("prusalink", PrintSource.PrusaLink)]
    [InlineData("bambu", PrintSource.Bambu)]
    [InlineData("sdcp", PrintSource.Sdcp)]
    [InlineData("duet", PrintSource.Duet)]
    [InlineData("flashforge", PrintSource.FlashForge)]
    [InlineData("anycubic", PrintSource.Anycubic)]
    [InlineData("PrusaLink", PrintSource.PrusaLink)]
    public void ANewAdapterKind_NamesThePrintSource(string kind, PrintSource expected)
    {
        Assert.Equal(expected, Connection.PrintSourceForKind(kind));
    }

    [Theory]
    [InlineData("moonraker")]
    [InlineData("something-else")]
    [InlineData(null)]
    public void MoonrakerAndUnknownKinds_NameNoPrintSource(string? kind)
    {
        // Moonraker bridge prints have always been ApiKey; see Connection.PrintSourceForKind.
        Assert.Null(Connection.PrintSourceForKind(kind));
    }

    [Fact]
    public void PrintSourceNumbers_NeverChange()
    {
        // Integers on the wire (#152): the web app, plugins and connectors send and read these.
        Assert.Equal(
            [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12],
            Enum.GetValues<PrintSource>().Select(s => (int)s));
        Assert.Equal(7, (int)PrintSource.PrusaLink);
        Assert.Equal(8, (int)PrintSource.Bambu);
        Assert.Equal(9, (int)PrintSource.Sdcp);
        Assert.Equal(10, (int)PrintSource.Duet);
        Assert.Equal(11, (int)PrintSource.FlashForge);
        Assert.Equal(12, (int)PrintSource.Anycubic);
    }
}
