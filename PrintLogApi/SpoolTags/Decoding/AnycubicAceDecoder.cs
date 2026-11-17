using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PrintLogApi.SpoolTags.Decoding;

/// <summary>
/// Anycubic ACE spool tags: an NTAG213-class Type 2 tag whose first 128 bytes hold the filament
/// record. Offsets index the raw memory from page 0. Integers are unsigned little-endian; strings
/// are ASCII cut at the first NUL. The format carries no signature, so nothing needs redacting.
/// </summary>
public sealed class AnycubicAceDecoder : ITagDecoder
{
    public const int CurrentVersion = 1;

    private const string FormatName = "anycubic-ace";
    private const int RecordBytes = 128;
    private const int MagicOffset = 16;
    private const string AnycubicBrandCode = "AC";
    private const string AnycubicBrandName = "Anycubic";

    private static readonly byte[] Magic = [0x7B, 0x00, 0x65, 0x00];

    public string Format => FormatName;

    public int Version => CurrentVersion;

    public bool CanDecode(TagMemory memory)
    {
        return memory.Kind == TagMemoryKind.Type2
            && memory.Data.Length >= RecordBytes
            && memory.Data.AsSpan(MagicOffset, Magic.Length).SequenceEqual(Magic);
    }

    public TagReading Decode(string uid, TagMemory memory)
    {
        var d = memory.Data;
        var r = new TagReading
        {
            Format = Format,
            DecoderVersion = Version,
            Sku = Text(d, 20, 16),
            MaterialType = Text(d, 60, 16),
        };

        var brandCode = Text(d, 40, 16);
        if (brandCode == AnycubicBrandCode)
        {
            r.Brand = AnycubicBrandName;
            r.Extras["brandCode"] = brandCode;
        }
        else
        {
            r.Brand = brandCode;
        }

        // Bytes 80-83 are stored A, B, G, R.
        r.Alpha = d[80];
        r.Colors.Add(Convert.ToHexString([d[83], d[82], d[81]]));

        AddWhenNonZero(r, "speedMinMmS", U16(d, 92));
        AddWhenNonZero(r, "speedMaxMmS", U16(d, 94));

        r.HotendMinC = U16(d, 96);
        r.HotendMaxC = U16(d, 98);
        r.BedMinC = U16(d, 116);
        r.BedMaxC = U16(d, 118);
        r.DiameterMm = U16(d, 120) / 100.0;
        r.LengthM = U16(d, 122);
        r.NetWeightG = U16(d, 124);

        return r;
    }

    public byte[] Redact(TagMemory memory)
    {
        return (byte[])memory.Data.Clone();
    }

    private static void AddWhenNonZero(TagReading reading, string key, int value)
    {
        if (value != 0)
        {
            reading.Extras[key] = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static int U16(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    }

    private static string? Text(byte[] data, int offset, int length)
    {
        var span = data.AsSpan(offset, length);
        var nul = span.IndexOf((byte)0);
        if (nul >= 0)
        {
            span = span[..nul];
        }

        var text = Encoding.ASCII.GetString(span).Trim();
        return text.Length == 0 ? null : text;
    }
}
