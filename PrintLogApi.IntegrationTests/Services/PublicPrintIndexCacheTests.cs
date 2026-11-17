using System.Buffers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using PrintLogApi.Caching;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Sitemap;
using PrintLogApi.Services.PublicPrints;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// <c>GET api/Prints/public</c> and <c>public/sitemap</c> read one cached index of the public
/// prints (api#309): the query behind them took 60 s and more on the production database, and
/// crawlers and the sitemap generator ask for it far more often than it changes.
/// </summary>
public class PublicPrintIndexCacheTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task EvictAsync() => factory.Services.GetRequiredService<HybridCache>().RemoveAsync(PublicPrintIndexCache.CacheKey, Ct).AsTask();

    private async Task<long> AddPrintAsync(PrintViewStatus viewStatus)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = new Print
        {
            Title = "Public index probe",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Success,
            ViewStatus = viewStatus,
            CreatedById = IntegrationTestSeeder.TestUserId,
            UpdatedById = IntegrationTestSeeder.TestUserId,
        };
        db.Prints.Add(print);
        await db.SaveChangesAsync(Ct);
        return print.Id;
    }

    private async Task<(List<long> Ids, List<SitemapEntryDto> Sitemap)> ReadBothAsync() => (
        (await _client.GetFromJsonAsync<List<long>>("/api/Prints/public", Ct))!,
        (await _client.GetFromJsonAsync<List<SitemapEntryDto>>("/api/Prints/public/sitemap", Ct))!);

    [Fact]
    public async Task BothLists_AreServedFromOneCachedRead_UntilItIsEvicted()
    {
        await EvictAsync();
        await ReadBothAsync();

        var added = await AddPrintAsync(PrintViewStatus.Public);
        var (ids, sitemap) = await ReadBothAsync();
        Assert.DoesNotContain(added, ids);
        Assert.DoesNotContain(sitemap, e => e.Id == added);

        await EvictAsync();
        (ids, sitemap) = await ReadBothAsync();
        Assert.Contains(added, ids);
        Assert.Contains(sitemap, e => e.Id == added);
    }

    [Fact]
    public async Task TheIndex_HoldsOnlyPublicPrints_InIdOrder_WithTheirLastSave()
    {
        var hidden = await AddPrintAsync(PrintViewStatus.Private);
        var shown = await AddPrintAsync(PrintViewStatus.Public);
        await EvictAsync();

        var (ids, sitemap) = await ReadBothAsync();

        Assert.DoesNotContain(hidden, ids);
        Assert.Contains(shown, ids);
        Assert.Equal(ids.Order(), ids);
        Assert.Equal(ids, sitemap.Select(e => e.Id));
        Assert.NotNull(Assert.Single(sitemap, e => e.Id == shown).LastModified);
    }

    [Fact]
    public void TheSerializer_RoundTripsIdsAndTimestamps_IncludingAnUnsetOne()
    {
        var saved = new DateTime(2026, 9, 14, 8, 30, 15, 123, DateTimeKind.Unspecified);
        var index = new PublicPrintIndex([3, 17, 400_000], [saved.Ticks, DateTime.MinValue.Ticks, saved.AddDays(1).Ticks]);

        var copy = RoundTrip(index, out _);

        Assert.Equal(index.Ids, copy.Ids);
        Assert.Equal(index.ToSitemapEntries(), copy.ToSitemapEntries());
        Assert.Null(copy.ToSitemapEntries()[1].LastModified);
        Assert.Equal(new DateTimeOffset(saved, TimeSpan.Zero), copy.ToSitemapEntries()[0].LastModified);
    }

    /// <summary>
    /// HybridCache refuses (and silently stops caching) a payload over its
    /// <c>MaximumPayloadBytes</c>, and charges what it stores against the shared 8 MiB budget.
    /// Production had about 31,000 public prints out of 412,000 ids in November 2026; this
    /// checks three times that still fits under both with room.
    /// </summary>
    [Fact]
    public void ThreeTimesTheProductionIndex_StaysUnderThePayloadLimit_AndAnEighthOfTheBudget()
    {
        const int rows = 93_000;
        var ids = Enumerable.Range(1, rows).Select(i => (long)i * 13).ToArray();
        var ticks = Enumerable.Range(0, rows).Select(i => new DateTime(2024, 1, 1).AddMinutes(i * 7.3).Ticks).ToArray();

        RoundTrip(new PublicPrintIndex(ids, ticks), out var bytes);

        var limit = factory.Services.GetRequiredService<IOptions<HybridCacheOptions>>().Value.MaximumPayloadBytes;
        Assert.True(bytes < limit, $"{bytes} bytes is over HybridCache's {limit}-byte payload limit.");
        Assert.True(bytes < CacheBudget.SizeLimitBytes / 8, $"{bytes} bytes is more than an eighth of the cache budget.");
    }

    private static PublicPrintIndex RoundTrip(PublicPrintIndex index, out long bytes)
    {
        var serializer = new PublicPrintIndexSerializer();
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(index, buffer);
        bytes = buffer.WrittenCount;
        return serializer.Deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }
}
