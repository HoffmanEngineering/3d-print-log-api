using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PrintLogApi.SpoolTags.Decoding;

/// <summary>
/// Bambu Lab spool tags: a MIFARE Classic 1K laid out as in the Bambu Research Group's RFID Tag
/// Guide. Sectors 10-15 hold an RSA signature. Offsets index the raw 1K memory, trailers included.
/// Integers are unsigned little-endian; strings are ASCII cut at the first NUL.
/// </summary>
public sealed class BambuDecoder : ITagDecoder
{
    private const string KeyFormatName = "bambu";
    private const string Maker = "Bambu Lab";
    private const int SignatureStart = 640;

    public string Format => KeyFormatName;

    public int Version => 1;

    public bool CanDecode(TagMemory memory)
    {
        return memory.Kind == TagMemoryKind.Classic
            && string.Equals(memory.KeyFormat, KeyFormatName, StringComparison.OrdinalIgnoreCase)
            && memory.Data.Length == TagMemory.ClassicBytes
            && Text(memory.Data, 32, 16) is not null;
    }

    public TagReading Decode(string uid, TagMemory memory)
    {
        var d = memory.Data;
        var type = Text(d, 32, 16);
        var r = new TagReading
        {
            Format = Format,
            DecoderVersion = Version,
            // Bambu makes its own filament, so the brand is also the manufacturer.
            Brand = Maker,
            Manufacturer = Maker,
            MaterialType = type,
            MaterialSubtype = Subtype(type, Text(d, 64, 16)),
            Sku = Text(d, 16, 8),
        };

        if (Text(d, 24, 8) is { } materialId)
        {
            r.Extras["materialId"] = materialId;
        }

        r.Colors.Add(Convert.ToHexString(d, 80, 3));
        r.Alpha = d[83];

        // Block 16 stores a second color as ABGR when the spool has two.
        if (U16(d, 258) == 2 && (d[261] | d[262] | d[263]) != 0)
        {
            r.Colors.Add(Convert.ToHexString([d[263], d[262], d[261]]));
        }

        r.NetWeightG = U16(d, 84);
        r.DiameterMm = Math.Round(BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(88, 4)), 2);
        r.DryTempC = U16(d, 96);
        r.DryHours = U16(d, 98);

        var bedType = U16(d, 100);
        if (bedType != 0)
        {
            r.Extras["bedType"] = bedType.ToString(CultureInfo.InvariantCulture);
        }

        var bed = U16(d, 102);
        r.BedMinC = bed == 0 ? null : bed;
        r.BedMaxC = r.BedMinC;
        r.HotendMaxC = U16(d, 104);
        r.HotendMinC = U16(d, 106);
        r.ManufacturedOn = ParseDate(Text(d, 192, 16));
        r.LengthM = U16(d, 228);

        return r;
    }

    public byte[] Redact(TagMemory memory)
    {
        var copy = (byte[])memory.Data.Clone();
        if (copy.Length > SignatureStart)
        {
            Array.Clear(copy, SignatureStart, copy.Length - SignatureStart);
        }

        return copy;
    }

    /// <summary>"PLA Basic" on a PLA spool is "Basic"; a detailed type that doesn't start with the material is kept whole.</summary>
    private static string? Subtype(string? type, string? detailed)
    {
        if (detailed is null || string.Equals(detailed, type, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (type is not null && detailed.StartsWith(type + " ", StringComparison.OrdinalIgnoreCase))
        {
            return detailed[(type.Length + 1)..].Trim();
        }

        return detailed;
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

    /// <summary>The tag stores <c>yyyy_MM_dd_HH_mm</c>; only the date is kept.</summary>
    private static DateOnly? ParseDate(string? text)
    {
        if (text is null
            || text.Length < 10
            || !DateOnly.TryParseExact(text[..10], "yyyy_MM_dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || date == new DateOnly(1970, 1, 1))
        {
            return null;
        }

        return date;
    }
}
