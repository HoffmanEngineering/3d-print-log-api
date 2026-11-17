using System.Buffers.Binary;
using System.Text;
using PrintLogApi.SpoolTags;
using PrintLogApi.SpoolTags.Decoding;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

public class BambuDecoderTests
{
    private const string Fixture = "bambu-AEF31F97";

    private readonly BambuDecoder _decoder = new();

    private static TagMemory Classic(byte[] data, string? keyFormat = "bambu")
    {
        return new TagMemory(TagMemoryKind.Classic, data, keyFormat, []);
    }

    private static byte[] FixtureBytes()
    {
        return (byte[])TagFixtures.Memory(Fixture).Data.Clone();
    }

    private static void WriteText(byte[] data, int offset, string text)
    {
        Array.Clear(data, offset, 16);
        Encoding.ASCII.GetBytes(text).CopyTo(data, offset);
    }

    [Fact]
    public void Identity()
    {
        Assert.Equal("bambu", _decoder.Format);
        Assert.Equal(1, _decoder.Version);
    }

    [Fact]
    public void CanDecode_TrueForTheFixture()
    {
        Assert.True(_decoder.CanDecode(TagFixtures.Memory(Fixture)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("snapmaker")]
    public void CanDecode_FalseWithoutTheBambuKeyFormat(string? keyFormat)
    {
        Assert.False(_decoder.CanDecode(Classic(FixtureBytes(), keyFormat)));
    }

    [Fact]
    public void CanDecode_FalseForZeroedMemory()
    {
        Assert.False(_decoder.CanDecode(Classic(new byte[1024])));
    }

    [Fact]
    public void CanDecode_FalseForNonClassicMemory()
    {
        var memory = new TagMemory(TagMemoryKind.Type2, FixtureBytes(), "bambu", []);
        Assert.False(_decoder.CanDecode(memory));
    }

    [Fact]
    public void Decode_PlaBasic()
    {
        var r = _decoder.Decode("AEF31F97", TagFixtures.Memory(Fixture));

        Assert.Equal("bambu", r.Format);
        Assert.Equal(1, r.DecoderVersion);
        Assert.Equal("Bambu Lab", r.Brand);
        Assert.Equal("Bambu Lab", r.Manufacturer);
        Assert.Equal("PLA", r.MaterialType);
        Assert.Equal("Basic", r.MaterialSubtype);
        Assert.Null(r.MaterialSubtypeCode);
        Assert.Equal(["6F5034"], r.Colors);
        Assert.Equal(255, r.Alpha);
        Assert.Equal("A00-N1", r.Sku);
        Assert.Equal("GFA00", r.Extras["materialId"]);
        Assert.Equal(1.75, r.DiameterMm);
        Assert.Equal(1000, r.NetWeightG);
        Assert.Equal(330, r.LengthM);
        Assert.Equal(55, r.DryTempC);
        Assert.Equal(8, r.DryHours);
        Assert.Equal(190, r.HotendMinC);
        Assert.Equal(230, r.HotendMaxC);
        Assert.Null(r.HotendC);
        // This tag leaves the bed temperature at 0, which means not set.
        Assert.Null(r.BedMinC);
        Assert.Null(r.BedMaxC);
        Assert.Equal(new DateOnly(2026, 6, 14), r.ManufacturedOn);
        Assert.False(r.Extras.ContainsKey("bedType"));
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Decode_BedTemperatureAndTypeWhenSet()
    {
        var data = FixtureBytes();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(100, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(102, 2), 65);

        var r = _decoder.Decode("AEF31F97", Classic(data));

        Assert.Equal(65, r.BedMinC);
        Assert.Equal(65, r.BedMaxC);
        Assert.Equal("1", r.Extras["bedType"]);
    }

    [Theory]
    [InlineData("PLA Silk+", "Silk+")]
    [InlineData("PLA", null)]
    [InlineData("Support for PLA", "Support for PLA")]
    [InlineData("", null)]
    public void Decode_SubtypeIsTheDetailedTypeWithoutTheMaterial(string detailed, string? subtype)
    {
        var data = FixtureBytes();
        WriteText(data, 64, detailed);

        Assert.Equal(subtype, _decoder.Decode("AEF31F97", Classic(data)).MaterialSubtype);
    }

    [Fact]
    public void Decode_ReadsTheSecondColorOfATwoColorSpool()
    {
        var data = FixtureBytes();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(258, 2), 2);
        // Stored as ABGR.
        data[260] = 0xFF;
        data[261] = 0x33;
        data[262] = 0x22;
        data[263] = 0x11;

        Assert.Equal(["6F5034", "112233"], _decoder.Decode("AEF31F97", Classic(data)).Colors);
    }

    [Fact]
    public void Decode_IgnoresAnEmptySecondColor()
    {
        var data = FixtureBytes();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(258, 2), 2);
        Array.Clear(data, 260, 4);

        Assert.Equal(["6F5034"], _decoder.Decode("AEF31F97", Classic(data)).Colors);
    }

    [Fact]
    public void Decode_EmptyVariantIdIsNoSku()
    {
        var data = FixtureBytes();
        Array.Clear(data, 16, 8);

        Assert.Null(_decoder.Decode("AEF31F97", Classic(data)).Sku);
    }

    [Theory]
    [InlineData("1970_01_01_00_00")]
    [InlineData("2026_13_45_00_00")]
    [InlineData("not a date")]
    [InlineData("")]
    public void Decode_BadManufactureDateIsNull(string date)
    {
        var data = FixtureBytes();
        WriteText(data, 192, date);

        Assert.Null(_decoder.Decode("AEF31F97", Classic(data)).ManufacturedOn);
    }

    [Fact]
    public void Redact_ZeroesTheSignatureRegionOnACopy()
    {
        var data = FixtureBytes();
        Array.Fill(data, (byte)0xAB, 640, 384);

        var redacted = _decoder.Redact(Classic(data));

        Assert.NotSame(data, redacted);
        Assert.All(redacted[640..], b => Assert.Equal(0, b));
        Assert.Equal(data[..640], redacted[..640]);
        Assert.Equal(0xAB, data[700]);
    }

    [Fact]
    public void Fixture_HasNoSignatureBytesOrKeys()
    {
        var data = TagFixtures.Memory(Fixture).Data;

        Assert.All(data[640..], b => Assert.Equal(0, b));
        for (var sector = 0; sector < 16; sector++)
        {
            var trailer = sector * 64 + 48;
            Assert.All(data[trailer..(trailer + 6)], b => Assert.Equal(0, b));
            Assert.All(data[(trailer + 10)..(trailer + 16)], b => Assert.Equal(0, b));
        }
    }
}
