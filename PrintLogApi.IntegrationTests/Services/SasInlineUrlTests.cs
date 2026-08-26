using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

public class SasInlineUrlTests
{
    private static readonly TimeSpan Bucket = TimeSpan.FromHours(6);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(5);

    private static AzureBlobStorageService CreateService(TimeProvider clock)
    {
        // Syntactically valid throwaway credentials. No network call happens:
        // SAS generation is a local HMAC.
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AZURE_STORAGE_CONNECTION_STRING"] =
                    "DefaultEndpointsProtocol=https;AccountName=testaccount;" +
                    "AccountKey=dGVzdGtleXRlc3RrZXl0ZXN0a2V5dGVzdGtleXRlc3RrZXk=;" +
                    "EndpointSuffix=core.windows.net"
            }).Build();

        return new AzureBlobStorageService(config, clock, new BlobContainerProvisioner());
    }

    [Fact]
    public async Task TwoCallsInsideOneBucket_ProduceIdenticalUrls()
    {
        var clock = new SettableTimeProvider(new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero));
        var service = CreateService(clock);

        var first = await service.GenerateSasInlineUrlAsync(
            BlobContainers.FilamentImages, "a.webp", "image/webp", Bucket, MaxAge);

        clock.SetUtcNow(new DateTimeOffset(2026, 8, 25, 3, 0, 0, TimeSpan.Zero));

        var second = await service.GenerateSasInlineUrlAsync(
            BlobContainers.FilamentImages, "a.webp", "image/webp", Bucket, MaxAge);

        // Byte-identical URLs are the entire reason SAS beats the proxy. A browser
        // keys its image cache on the URL; an unstable URL never hits.
        Assert.Equal(first.ToString(), second.ToString());
    }

    [Fact]
    public async Task CallsAcrossABucketBoundary_ProduceDifferentUrls()
    {
        var clock = new SettableTimeProvider(new DateTimeOffset(2026, 8, 25, 5, 0, 0, TimeSpan.Zero));
        var service = CreateService(clock);

        var first = await service.GenerateSasInlineUrlAsync(
            BlobContainers.FilamentImages, "a.webp", "image/webp", Bucket, MaxAge);

        clock.SetUtcNow(new DateTimeOffset(2026, 8, 25, 7, 0, 0, TimeSpan.Zero));

        var second = await service.GenerateSasInlineUrlAsync(
            BlobContainers.FilamentImages, "a.webp", "image/webp", Bucket, MaxAge);

        Assert.NotEqual(first.ToString(), second.ToString());
    }

    [Fact]
    public async Task Url_IsInlineAndPrivatelyCacheable()
    {
        var clock = new SettableTimeProvider(new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero));
        var service = CreateService(clock);

        var uri = await service.GenerateSasInlineUrlAsync(
            BlobContainers.FilamentImages, "a.webp", "image/webp", Bucket, MaxAge);

        var query = Uri.UnescapeDataString(uri.Query);

        Assert.Contains("inline", query);
        Assert.DoesNotContain("attachment", query);
        Assert.Contains("private", query);
        Assert.Contains("max-age=18000", query);
    }

    [Fact]
    public async Task Signature_NeverOutlivesTwoBuckets()
    {
        // Bucketing rounds the expiry UP and then adds another full bucket, so a signature
        // signed just after a boundary lives nearly two bucket widths - not the one width
        // the configured constant suggests. That is deliberate (a URL signed just before a
        // boundary would otherwise expire immediately), but it is the real exposure window
        // for a leaked URL, so pin it.
        var justAfterBoundary = new DateTimeOffset(2026, 8, 25, 0, 0, 1, TimeSpan.Zero);
        var clock = new SettableTimeProvider(justAfterBoundary);
        var service = CreateService(clock);

        var uri = await service.GenerateSasInlineUrlAsync(
            BlobContainers.FilamentImages, "a.webp", "image/webp", Bucket, MaxAge);

        var expiry = DateTimeOffset.Parse(
            System.Web.HttpUtility.ParseQueryString(uri.Query)["se"]!,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal);

        var lifetime = expiry - justAfterBoundary;

        Assert.True(lifetime > Bucket, $"expected more than one bucket, got {lifetime}");
        Assert.True(lifetime <= Bucket + Bucket, $"expected at most two buckets, got {lifetime}");
    }

    [Fact]
    public async Task MaxAgeNotShorterThanBucket_Throws()
    {
        // Exercises the guard itself. Asserting `MaxAge < Bucket` on two constants
        // proves nothing about the implementation.
        var clock = new SettableTimeProvider(DateTimeOffset.UtcNow);
        var service = CreateService(clock);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GenerateSasInlineUrlAsync(
                BlobContainers.FilamentImages, "a.webp", "image/webp",
                TimeSpan.FromHours(6), TimeSpan.FromHours(6)));
    }
}
