using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Models.DTOs.Sitemap;

namespace PrintLogApi.Services.PublicPrints;

/// <summary>
/// Every public print's id and last save, in id order: what <c>GET api/Prints/public</c> and
/// <c>public/sitemap</c> both serve. Two parallel arrays rather than a list of entries, because
/// it is about 31,000 rows (November 2026) and held for the life of the cache entry.
/// Read-only once built: HybridCache hands every caller this same instance.
/// </summary>
[ImmutableObject(true)]
public sealed class PublicPrintIndex
{
    private readonly long[] _ids;
    private readonly long[] _updatedTicks;

    /// <param name="ids">Public print ids, ascending.</param>
    /// <param name="updatedTicks">Each print's <c>UpdatedDate</c> as stored (UTC, read back unspecified), in ticks.</param>
    public PublicPrintIndex(long[] ids, long[] updatedTicks)
    {
        if (ids.Length != updatedTicks.Length)
        {
            throw new ArgumentException("Every id needs a timestamp.", nameof(updatedTicks));
        }

        _ids = ids;
        _updatedTicks = updatedTicks;
    }

    public int Count => _ids.Length;

    public IReadOnlyList<long> Ids => _ids;

    internal long UpdatedTicksAt(int i) => _updatedTicks[i];

    /// <summary>Built per request (cheap next to the query it replaces), so nothing shared is handed out mutable.</summary>
    public List<SitemapEntryDto> ToSitemapEntries()
    {
        var entries = new List<SitemapEntryDto>(_ids.Length);
        for (var i = 0; i < _ids.Length; i++)
        {
            entries.Add(SitemapEntryDto.FromUpdatedDate(_ids[i], new DateTime(_updatedTicks[i], DateTimeKind.Unspecified)));
        }

        return entries;
    }
}

/// <summary>
/// A compact binary form for <see cref="PublicPrintIndex"/>. HybridCache serializes every entry,
/// even one it shares by reference, to charge its size against the 8 MiB budget, and it stops
/// caching (silently, apart from a log line) any entry over its 1 MiB payload limit. At 31,000
/// rows the sitemap entries as JSON would be nearly 2 MB, and these two arrays as JSON about
/// 0.8 MB, close to the limit; here each row is a 7-bit-encoded id delta (one or two
/// bytes, as ids ascend) and eight bytes of ticks, about 300 KB, so the limit is roughly 100,000
/// public prints away. <c>PublicPrintIndexCacheTests</c> checks three times today's size.
/// </summary>
public sealed class PublicPrintIndexSerializer : IHybridCacheSerializer<PublicPrintIndex>
{
    private const byte Version = 1;

    public void Serialize(PublicPrintIndex value, IBufferWriter<byte> target)
    {
        var span = target.GetSpan(1 + 5);
        span[0] = Version;
        var written = 1 + Write7Bit(span[1..], (ulong)value.Count);
        target.Advance(written);

        var previous = 0L;
        for (var i = 0; i < value.Count; i++)
        {
            var id = value.Ids[i];
            span = target.GetSpan(10 + 8);
            var length = Write7Bit(span, unchecked((ulong)(id - previous)));
            BinaryPrimitives.WriteInt64LittleEndian(span[length..], value.UpdatedTicksAt(i));
            target.Advance(length + 8);
            previous = id;
        }
    }

    public PublicPrintIndex Deserialize(ReadOnlySequence<byte> source)
    {
        var reader = new SequenceReader<byte>(source);
        if (!reader.TryRead(out var version) || version != Version)
        {
            throw new InvalidDataException($"Unknown public print index format {version}.");
        }

        var count = checked((int)Read7Bit(ref reader));
        var ids = new long[count];
        var ticks = new long[count];
        var previous = 0L;
        for (var i = 0; i < count; i++)
        {
            previous = unchecked(previous + (long)Read7Bit(ref reader));
            ids[i] = previous;
            if (!reader.TryReadLittleEndian(out ticks[i]))
            {
                throw new InvalidDataException("The public print index is truncated.");
            }
        }

        return new PublicPrintIndex(ids, ticks);
    }

    private static int Write7Bit(Span<byte> span, ulong value)
    {
        var i = 0;
        while (value >= 0x80)
        {
            span[i++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[i++] = (byte)value;
        return i;
    }

    private static ulong Read7Bit(ref SequenceReader<byte> reader)
    {
        ulong result = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (!reader.TryRead(out var b))
            {
                throw new InvalidDataException("The public print index is truncated.");
            }

            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return result;
            }
        }

        throw new InvalidDataException("The public print index has a malformed length.");
    }
}
