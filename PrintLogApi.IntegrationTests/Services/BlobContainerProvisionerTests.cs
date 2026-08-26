using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

public class BlobContainerProvisionerTests
{
    [Fact]
    public void ContainerNames_AreLowercaseAndStable()
    {
        // Load-bearing: existing blobs already live under these names.
        Assert.Equal("printimages", BlobContainers.PrintImages);
        Assert.Equal("projectimages", BlobContainers.ProjectImages);
        Assert.Equal("filamentimages", BlobContainers.FilamentImages);
    }

    [Fact]
    public async Task Ensure_RunsTheFactoryOnlyOncePerContainer()
    {
        var calls = 0;
        var provisioner = new BlobContainerProvisioner();

        for (var i = 0; i < 5; i++)
        {
            await provisioner.EnsureAsync(
                BlobContainers.FilamentImages,
                () =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.CompletedTask;
                },
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Ensure_RunsOncePerContainerUnderConcurrency()
    {
        var calls = 0;
        var provisioner = new BlobContainerProvisioner();

        // ConcurrentDictionary.GetOrAdd can invoke its value factory more than
        // once under contention. Lazy<Task> is what makes "exactly once" true.
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            provisioner.EnsureAsync(
                BlobContainers.FilamentImages,
                async () =>
                {
                    Interlocked.Increment(ref calls);
                    await Task.Yield();
                },
                TestContext.Current.CancellationToken)));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Ensure_DoesNotCacheAFailure()
    {
        var calls = 0;
        var provisioner = new BlobContainerProvisioner();

        Task Flaky()
        {
            calls++;
            return calls == 1
                ? Task.FromException(new InvalidOperationException("blip"))
                : Task.CompletedTask;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.EnsureAsync(
                BlobContainers.FilamentImages,
                Flaky,
                TestContext.Current.CancellationToken));

        // A cached failure would break uploads for the life of the process after a
        // single transient storage blip.
        await provisioner.EnsureAsync(
            BlobContainers.FilamentImages,
            Flaky,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
    }
}
