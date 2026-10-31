using PrintLogApi.Exceptions;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>The memory budget every image decode shares: what it lets through, in what order, and when it gives up.</summary>
public class ImageMemoryGateTests
{
    private const long Mb = 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ImageMemoryGate Gate(long budgetMb = 100, double waitSeconds = 5) => new(budgetMb * Mb, TimeSpan.FromSeconds(waitSeconds));

    [Fact]
    public async Task WithinTheBudget_LeasesAreGrantedAtOnce_AndDisposingReturnsThem()
    {
        var gate = Gate();

        var a = await gate.AcquireAsync(40 * Mb, Ct);
        var b = await gate.AcquireAsync(60 * Mb, Ct);
        Assert.Equal(0, gate.AvailableBytes);

        a.Dispose();
        a.Dispose(); // A second dispose returns nothing more.
        Assert.Equal(40 * Mb, gate.AvailableBytes);

        b.Dispose();
        Assert.Equal(100 * Mb, gate.AvailableBytes);
    }

    [Fact]
    public async Task OverTheBudget_ALeaseWaitsForRoom()
    {
        var gate = Gate();
        var held = await gate.AcquireAsync(80 * Mb, Ct);

        var waiting = gate.AcquireAsync(40 * Mb, Ct);
        await Task.Delay(50, Ct);
        Assert.False(waiting.IsCompleted);

        held.Dispose();
        using var granted = await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(60 * Mb, gate.AvailableBytes);
    }

    [Fact]
    public async Task TheLineIsFirstInFirstOut_SoALargeImageIsNotStarvedBySmallOnes()
    {
        var gate = Gate();
        var held = await gate.AcquireAsync(60 * Mb, Ct);

        var large = gate.AcquireAsync(90 * Mb, Ct);

        // 40 MB is free, but the large image is ahead in line: the small one waits behind it.
        var small = gate.AcquireAsync(10 * Mb, Ct);
        await Task.Delay(50, Ct);
        Assert.False(small.IsCompleted);

        held.Dispose();
        var largeLease = await large.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        using var smallLease = await small.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        largeLease.Dispose();
        Assert.Equal(90 * Mb, gate.AvailableBytes);
    }

    [Fact]
    public async Task AChargeLargerThanTheBudget_RunsAlone_InsteadOfNever()
    {
        var gate = Gate();

        using var lease = await gate.AcquireAsync(500 * Mb, Ct);

        Assert.Equal(0, gate.AvailableBytes);
    }

    [Fact]
    public async Task AWaitPastMaxWait_IsBusy_AndLeavesTheLine()
    {
        var gate = Gate(waitSeconds: 0.1);
        var held = await gate.AcquireAsync(100 * Mb, Ct);

        await Assert.ThrowsAsync<ImageProcessingBusyException>(() => gate.AcquireAsync(50 * Mb, Ct));

        // The one that gave up no longer holds a place: the next one goes straight in.
        held.Dispose();
        using var next = await gate.AcquireAsync(100 * Mb, Ct);
        Assert.Equal(0, gate.AvailableBytes);
    }

    [Fact]
    public async Task AWaiterThatGivesUp_LetsTheOnesBehindItIn()
    {
        var gate = Gate(waitSeconds: 0.2);
        using var held = await gate.AcquireAsync(50 * Mb, Ct);

        // Ahead in line, and never fits while held is out.
        var blocked = gate.AcquireAsync(100 * Mb, Ct);
        await Task.Delay(20, Ct);
        var behind = gate.AcquireAsync(30 * Mb, Ct);

        await Assert.ThrowsAsync<ImageProcessingBusyException>(() => blocked);
        using var lease = await behind.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(20 * Mb, gate.AvailableBytes);
    }

    [Fact]
    public async Task ACancelledWait_IsACancellation_NotBusy()
    {
        var gate = Gate();
        using var held = await gate.AcquireAsync(100 * Mb, Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var waiting = gate.AcquireAsync(10 * Mb, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, gate.AvailableBytes);
    }
}
