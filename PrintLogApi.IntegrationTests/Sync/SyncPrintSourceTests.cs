using PrintLogApi.Models;
using PrintLogApi.Services.Sync;
using Xunit;

namespace PrintLogApi.IntegrationTests.Sync;

/// <summary>The snapshot's <c>source</c> spelling, one per <see cref="PrintSource"/>.</summary>
public class SyncPrintSourceTests
{
    [Theory]
    [InlineData(PrintSource.PrusaLink, "prusalink")]
    [InlineData(PrintSource.Bambu, "bambu")]
    [InlineData(PrintSource.Sdcp, "sdcp")]
    public void TheAdapterSources_HaveTheirOwnNames(PrintSource source, string expected)
    {
        Assert.Equal(expected, SyncPrintSource.Name(source));
    }

    [Fact]
    public void EveryKnownSource_HasAName()
    {
        // Unknown is for rows from before the column; any other value reading "unknown" is a
        // missed arm.
        Assert.All(
            Enum.GetValues<PrintSource>().Where(s => s != PrintSource.Unknown),
            s => Assert.NotEqual("unknown", SyncPrintSource.Name(s)));
    }
}
