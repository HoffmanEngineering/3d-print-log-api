using PrintLogApi.SpoolTags;
using PrintLogApi.SpoolTags.Decoding;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

public class AnycubicAceDecoderTests
{
    private readonly AnycubicAceDecoder _decoder = new();

    private static byte[] FactoryBytes()
    {
        return (byte[])TagFixtures.Memory("anycubic-539D1881020001").Data.Clone();
    }

    [Fact]
    public void Identity()
    {
        Assert.Equal("anycubic-ace", _decoder.Format);
        Assert.Equal(AnycubicAceDecoder.CurrentVersion, _decoder.Version);
    }

    [Theory]
    [InlineData("anycubic-539D1881020001")]
    [InlineData("anycubic-dng-0416DE5A061F91")]
    public void CanDecode_TrueForFilamentTags(string fixture)
    {
        Assert.True(_decoder.CanDecode(TagFixtures.Memory(fixture)));
    }

    [Fact]
    public void CanDecode_FalseForTheBlankTag()
    {
        Assert.False(_decoder.CanDecode(TagFixtures.Memory("blank-ev1-0416C55A061F91")));
    }

    [Fact]
    public void CanDecode_FalseForClassicMemory()
    {
        var memory = new TagMemory(TagMemoryKind.Classic, new byte[TagMemory.ClassicBytes], null, []);
        Assert.False(_decoder.CanDecode(memory));
    }

    [Fact]
    public void CanDecode_FalseForClassicMemoryWithTheMagic()
    {
        var data = new byte[TagMemory.ClassicBytes];
        FactoryBytes().AsSpan(0, 128).CopyTo(data);
        Assert.False(_decoder.CanDecode(new TagMemory(TagMemoryKind.Classic, data, null, [])));
    }

    [Fact]
    public void CanDecode_FalseWhenShorterThan128Bytes()
    {
        var data = FactoryBytes().AsSpan(0, 127).ToArray();
        Assert.False(_decoder.CanDecode(new TagMemory(TagMemoryKind.Type2, data, null, [])));
    }

    [Fact]
    public void Decode_FactoryTag()
    {
        var r = _decoder.Decode("539D1881020001", TagFixtures.Memory("anycubic-539D1881020001"));

        Assert.Equal("anycubic-ace", r.Format);
        Assert.Equal(AnycubicAceDecoder.CurrentVersion, r.DecoderVersion);
        Assert.Equal("AHPLVO-107", r.Sku);
        Assert.Equal("Anycubic", r.Brand);
        Assert.Equal("AC", r.Extras["brandCode"]);
        Assert.Equal("PLA", r.MaterialType);
        Assert.Equal(["FF7F32"], r.Colors);
        Assert.Equal(255, r.Alpha);
        Assert.Equal("50", r.Extras["speedMinMmS"]);
        Assert.Equal("200", r.Extras["speedMaxMmS"]);
        Assert.Equal(190, r.HotendMinC);
        Assert.Equal(230, r.HotendMaxC);
        Assert.Equal(55, r.BedMinC);
        Assert.Equal(65, r.BedMaxC);
        Assert.Equal(1.75, r.DiameterMm);
        Assert.Equal(330, r.LengthM);
        Assert.Equal(1000, r.NetWeightG);
    }

    [Fact]
    public void Decode_DnGCraftsTag()
    {
        var r = _decoder.Decode("0416DE5A061F91", TagFixtures.Memory("anycubic-dng-0416DE5A061F91"));

        Assert.Equal("AHPLPBK-102", r.Sku);
        Assert.Equal("PLA+", r.MaterialType);
        Assert.Equal(["B1BEC6"], r.Colors);
        Assert.Equal(210, r.HotendMinC);
        Assert.Equal(230, r.HotendMaxC);
        Assert.Equal(45, r.BedMinC);
        Assert.Equal(60, r.BedMaxC);
        Assert.False(r.Extras.ContainsKey("speedMinMmS"));
        Assert.False(r.Extras.ContainsKey("speedMaxMmS"));
    }

    [Fact]
    public void Decode_KeepsAnUnknownBrandCodeAsIs()
    {
        var data = FactoryBytes();
        data[40] = (byte)'Z';
        data[41] = (byte)'Q';

        var r = _decoder.Decode("539D1881020001", new TagMemory(TagMemoryKind.Type2, data, null, []));

        Assert.Equal("ZQ", r.Brand);
        Assert.False(r.Extras.ContainsKey("brandCode"));
    }

    [Fact]
    public void Redact_ReturnsAnUnchangedCopy()
    {
        var memory = TagFixtures.Memory("anycubic-539D1881020001");

        var copy = _decoder.Redact(memory);

        Assert.Equal(memory.Data, copy);
        Assert.NotSame(memory.Data, copy);
    }
}
