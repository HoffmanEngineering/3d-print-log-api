using System.Text;
using PrintLogApi.SpoolTags;
using PrintLogApi.SpoolTags.Decoding;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

public class SnapmakerDecoderTests
{
    private readonly SnapmakerDecoder _decoder = new();

    private static TagMemory Classic(byte[] data, string? keyFormat = "snapmaker")
    {
        return new TagMemory(TagMemoryKind.Classic, data, keyFormat, []);
    }

    private static byte[] FixtureBytes()
    {
        return (byte[])TagFixtures.Memory("snapmaker-805BEC3E").Data.Clone();
    }

    [Fact]
    public void Identity()
    {
        Assert.Equal("snapmaker", _decoder.Format);
        Assert.Equal(1, _decoder.Version);
    }

    [Fact]
    public void CanDecode_TrueForTheFixture()
    {
        Assert.True(_decoder.CanDecode(TagFixtures.Memory("snapmaker-805BEC3E")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("other")]
    public void CanDecode_FalseWithoutTheSnapmakerKeyFormat(string? keyFormat)
    {
        Assert.False(_decoder.CanDecode(Classic(FixtureBytes(), keyFormat)));
    }

    [Fact]
    public void CanDecode_FalseForZeroedMemory()
    {
        Assert.False(_decoder.CanDecode(Classic(new byte[1024])));
    }

    [Fact]
    public void CanDecode_FalseForVersionAboveTen()
    {
        var data = FixtureBytes();
        data[64] = 11;
        Assert.False(_decoder.CanDecode(Classic(data)));
    }

    [Fact]
    public void CanDecode_FalseForNonClassicMemory()
    {
        var memory = new TagMemory(TagMemoryKind.Type2, FixtureBytes(), "snapmaker", []);
        Assert.False(_decoder.CanDecode(memory));
    }

    [Fact]
    public void Decode_SnapSpeedPla()
    {
        var r = _decoder.Decode("805BEC3E", TagFixtures.Memory("snapmaker-805BEC3E"));

        Assert.Equal("snapmaker", r.Format);
        Assert.Equal(1, r.DecoderVersion);
        Assert.Equal("Snapmaker", r.Brand);
        Assert.Equal("Polymaker", r.Manufacturer);
        Assert.Equal("PLA", r.MaterialType);
        Assert.Equal("SnapSpeed", r.MaterialSubtype);
        Assert.Equal(3, r.MaterialSubtypeCode);
        Assert.Equal(["080A0D"], r.Colors);
        Assert.Equal(255, r.Alpha);
        Assert.Equal("900001", r.Sku);
        Assert.Equal(1.75, r.DiameterMm);
        Assert.Equal(500, r.NetWeightG);
        Assert.Equal(150, r.LengthM);
        Assert.Equal(55, r.DryTempC);
        Assert.Equal(6, r.DryHours);
        Assert.Equal(190, r.HotendMinC);
        Assert.Equal(230, r.HotendMaxC);
        Assert.Equal(230, r.FirstLayerHotendC);
        Assert.Equal(220, r.HotendC);
        Assert.Equal(60, r.BedMinC);
        Assert.Equal(60, r.BedMaxC);
        Assert.Equal(new DateOnly(2026, 3, 12), r.ManufacturedOn);
        Assert.Equal("1", r.Extras["bedType"]);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Decode_UnknownSubtypeWarns()
    {
        var r = _decoder.Decode("50EDB4E2", TagFixtures.Memory("snapmaker-50EDB4E2"));

        Assert.Equal("PLA", r.MaterialType);
        Assert.Null(r.MaterialSubtype);
        Assert.Equal(13, r.MaterialSubtypeCode);
        Assert.Contains("unknown-subtype", r.Warnings);
        Assert.Equal(["D93B90"], r.Colors);
        Assert.Equal(227, r.Alpha);
        Assert.Equal(1000, r.NetWeightG);
        Assert.Equal(328, r.LengthM);
        Assert.Equal(65, r.BedMinC);
        Assert.Equal("34263", r.Sku);
        Assert.Equal(new DateOnly(2026, 6, 23), r.ManufacturedOn);
    }

    [Fact]
    public void Decode_UnknownMainTypeWarnsAndKeepsTheCode()
    {
        var data = FixtureBytes();
        data[66] = 9;

        var r = _decoder.Decode("805BEC3E", Classic(data));

        Assert.Null(r.MaterialType);
        Assert.Contains("unknown-main-type", r.Warnings);
        Assert.Equal("9", r.Extras["mainTypeCode"]);
    }

    [Fact]
    public void Decode_SubtypeZeroMeansNone()
    {
        var data = FixtureBytes();
        data[68] = 0;

        var r = _decoder.Decode("805BEC3E", Classic(data));

        Assert.Null(r.MaterialSubtype);
        Assert.Null(r.MaterialSubtypeCode);
        Assert.DoesNotContain("unknown-subtype", r.Warnings);
    }

    [Fact]
    public void Decode_ReadsEveryColorUpToTheCount()
    {
        var data = FixtureBytes();
        data[72] = 2;
        data[83] = 0x11;
        data[84] = 0x22;
        data[85] = 0x33;

        var r = _decoder.Decode("805BEC3E", Classic(data));

        Assert.Equal(["080A0D", "112233"], r.Colors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Decode_ColorCountOutsideOneToFiveIsOne(byte count)
    {
        var data = FixtureBytes();
        data[72] = count;

        Assert.Equal(["080A0D"], _decoder.Decode("805BEC3E", Classic(data)).Colors);
    }

    [Fact]
    public void Decode_ZeroSkuIsNull()
    {
        var data = FixtureBytes();
        Array.Clear(data, 96, 4);

        Assert.Null(_decoder.Decode("805BEC3E", Classic(data)).Sku);
    }

    [Theory]
    [InlineData("19700101")]
    [InlineData("2026abcd")]
    [InlineData("20261345")]
    [InlineData("")]
    public void Decode_BadManufactureDateIsNull(string date)
    {
        var data = FixtureBytes();
        Array.Clear(data, 160, 8);
        Encoding.ASCII.GetBytes(date).CopyTo(data, 160);

        Assert.Null(_decoder.Decode("805BEC3E", Classic(data)).ManufacturedOn);
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

    [Theory]
    [InlineData("snapmaker-805BEC3E")]
    [InlineData("snapmaker-50EDB4E2")]
    public void Fixtures_HaveNoSignatureBytes(string name)
    {
        var data = TagFixtures.Memory(name).Data;

        Assert.All(data[640..], b => Assert.Equal(0, b));
    }
}
