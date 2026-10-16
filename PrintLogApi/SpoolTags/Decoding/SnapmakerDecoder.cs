using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PrintLogApi.SpoolTags.Decoding;

/// <summary>
/// Snapmaker spool tags: a MIFARE Classic 1K whose first 640 bytes hold the filament record and
/// whose last 384 hold a signature. Offsets index the raw 1K memory, trailers included. Integers
/// are unsigned little-endian; strings are ASCII cut at the first NUL.
/// </summary>
public sealed class SnapmakerDecoder : ITagDecoder
{
    private const string KeyFormatName = "snapmaker";
    private const int SignatureStart = 640;
    private const int MinVersion = 1;
    private const int MaxVersion = 10;
    private const int MaxColors = 5;

    private static readonly string[] MainTypes = [null!, "PLA", "PETG", "ABS", "TPU", "PVA"];

    private static readonly string[] Subtypes = [null!, "Basic", "Matte", "SnapSpeed", "Silk", "Support", "HF", "95A", "95A HF"];

    public string Format => KeyFormatName;

    public int Version => 1;

    public bool CanDecode(TagMemory memory)
    {
        return memory is { Kind: TagMemoryKind.Classic, KeyFormat: KeyFormatName }
            && memory.Data.Length == TagMemory.ClassicBytes
            && U16(memory.Data, 64) is >= MinVersion and <= MaxVersion;
    }

    public TagReading Decode(string uid, TagMemory memory)
    {
        var d = memory.Data;
        var r = new TagReading
        {
            Format = Format,
            DecoderVersion = Version,
            Brand = Text(d, 16, 16),
            Manufacturer = Text(d, 32, 16),
        };

        var mainType = U16(d, 66);
        if (mainType < MainTypes.Length && MainTypes[mainType] is { } type)
        {
            r.MaterialType = type;
        }
        else
        {
            r.Warnings.Add("unknown-main-type");
            r.Extras["mainTypeCode"] = mainType.ToString(CultureInfo.InvariantCulture);
        }

        var subtype = U16(d, 68);
        if (subtype != 0)
        {
            r.MaterialSubtypeCode = subtype;
            if (subtype < Subtypes.Length)
            {
                r.MaterialSubtype = Subtypes[subtype];
            }
            else
            {
                r.Warnings.Add("unknown-subtype");
            }
        }

        var colorCount = d[72] is >= 1 and <= MaxColors ? d[72] : 1;
        for (var n = 0; n < colorCount; n++)
        {
            r.Colors.Add(Convert.ToHexString(d, 80 + 3 * n, 3));
        }

        r.Alpha = 255 - d[73];

        var sku = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(96, 4));
        r.Sku = sku == 0 ? null : sku.ToString(CultureInfo.InvariantCulture);

        r.DiameterMm = U16(d, 128) / 100.0;
        r.NetWeightG = U16(d, 130);
        r.LengthM = U16(d, 132);
        r.DryTempC = U16(d, 144);
        r.DryHours = U16(d, 146);
        r.HotendMaxC = U16(d, 148);
        r.HotendMinC = U16(d, 150);

        var bedType = U16(d, 152);
        if (bedType != 0)
        {
            r.Extras["bedType"] = bedType.ToString(CultureInfo.InvariantCulture);
        }

        r.BedMinC = U16(d, 154);
        r.BedMaxC = r.BedMinC;
        r.FirstLayerHotendC = U16(d, 156);
        r.HotendC = U16(d, 158);
        r.ManufacturedOn = ParseDate(Text(d, 160, 8));

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

    private static DateOnly? ParseDate(string? text)
    {
        if (text is null
            || !DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || date == new DateOnly(1970, 1, 1))
        {
            return null;
        }

        return date;
    }
}
