using System.Text;
using PrintLogApi.SpoolTags;
using PrintLogApi.SpoolTags.Decoding;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

public class NdefOpenSpoolDecoderTests
{
    private const string OpenSpoolJson =
        "{\"protocol\":\"openspool\",\"version\":\"1.0\",\"type\":\"PETG\",\"color_hex\":\"#1A2B3C\",\"brand\":\"Elegoo\","
        + "\"min_temp\":\"230\",\"max_temp\":\"250\",\"bed_min_temp\":70,\"bed_max_temp\":80,\"weight\":1000,\"diameter\":1.75}";

    private readonly NdefOpenSpoolDecoder _decoder = new();

    // A short MIME record: MB, ME, SR, TNF 2.
    private static byte[] JsonRecord(string json, string type = "application/json")
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var payload = Encoding.UTF8.GetBytes(json);
        return [0xD2, (byte)typeBytes.Length, (byte)payload.Length, .. typeBytes, .. payload];
    }

    // A non-short MIME record (4-byte payload length) so the payload can exceed 255 bytes.
    private static byte[] LongJsonRecord(string json)
    {
        var typeBytes = Encoding.ASCII.GetBytes("application/json");
        var payload = Encoding.UTF8.GetBytes(json);
        return [0xC2, (byte)typeBytes.Length, 0, 0, (byte)(payload.Length >> 8), (byte)payload.Length, .. typeBytes, .. payload];
    }

    private static byte[] Tlv(byte[] message, bool forceLongLength = false)
    {
        return message.Length >= 0xFF || forceLongLength
            ? [0x03, 0xFF, (byte)(message.Length >> 8), (byte)message.Length, .. message, 0xFE]
            : [0x03, (byte)message.Length, .. message, 0xFE];
    }

    private static TagMemory Type2WithNdef(byte[] message, bool forceLongLength = false)
    {
        var data = new byte[16 + 4 + message.Length + 8];
        new byte[] { 0xE1, 0x10, 0x12, 0x00 }.CopyTo(data, 12);
        Tlv(message, forceLongLength).CopyTo(data, 16);
        return new TagMemory(TagMemoryKind.Type2, data, null, []);
    }

    private static TagMemory NfcVWithNdef(byte[] message)
    {
        var data = new byte[4 + 4 + message.Length + 8];
        new byte[] { 0xE1, 0x40, 0x40, 0x00 }.CopyTo(data, 0);
        Tlv(message).CopyTo(data, 4);
        return new TagMemory(TagMemoryKind.NfcV, data, null, []);
    }

    private static void AssertOpenSpoolReading(TagReading r)
    {
        Assert.Equal("openspool", r.Format);
        Assert.Equal(NdefOpenSpoolDecoder.CurrentVersion, r.DecoderVersion);
        Assert.Equal("PETG", r.MaterialType);
        Assert.Equal(["1A2B3C"], r.Colors);
        Assert.Equal("Elegoo", r.Brand);
        Assert.Equal(230, r.HotendMinC);
        Assert.Equal(250, r.HotendMaxC);
        Assert.Equal(70, r.BedMinC);
        Assert.Equal(80, r.BedMaxC);
        Assert.Equal(1000, r.NetWeightG);
        Assert.Equal(1.75, r.DiameterMm);
    }

    [Fact]
    public void Identity()
    {
        Assert.Equal("openspool", _decoder.Format);
        Assert.Equal(NdefOpenSpoolDecoder.CurrentVersion, _decoder.Version);
    }

    [Fact]
    public void Decode_BlankTag_ThrowsBlankTagException()
    {
        var memory = TagFixtures.Memory("blank-ev1-0416C55A061F91");
        Assert.True(_decoder.CanDecode(memory));
        Assert.Throws<BlankTagException>(() => _decoder.Decode("0416C55A061F91", memory));
    }

    [Fact]
    public void Decode_OpenSpoolOnType2()
    {
        var memory = Type2WithNdef(JsonRecord(OpenSpoolJson));
        Assert.True(_decoder.CanDecode(memory));
        AssertOpenSpoolReading(_decoder.Decode("04", memory));
    }

    [Fact]
    public void Decode_OpenSpoolOnNfcV()
    {
        var memory = NfcVWithNdef(JsonRecord(OpenSpoolJson));
        Assert.True(_decoder.CanDecode(memory));
        AssertOpenSpoolReading(_decoder.Decode("E0", memory));
    }

    [Fact]
    public void Decode_NfcVWithZeroSizeByte_ReadsTlvsFromByte8()
    {
        var message = JsonRecord(OpenSpoolJson);
        var data = new byte[8 + 4 + message.Length + 4];
        new byte[] { 0xE2, 0x40, 0x00, 0x01 }.CopyTo(data, 0);
        Tlv(message).CopyTo(data, 8);
        AssertOpenSpoolReading(_decoder.Decode("E0", new TagMemory(TagMemoryKind.NfcV, data, null, [])));
    }

    [Fact]
    public void Decode_ThreeByteTlvLength()
    {
        var memory = Type2WithNdef(JsonRecord(OpenSpoolJson), forceLongLength: true);
        Assert.Equal(0xFF, memory.Data[17]);
        AssertOpenSpoolReading(_decoder.Decode("04", memory));
    }

    [Fact]
    public void Decode_LongPayloadWithFourByteRecordLengthAndThreeByteTlvLength()
    {
        var json = OpenSpoolJson.TrimEnd('}') + ",\"pad\":\"" + new string('x', 400) + "\"}";
        var memory = Type2WithNdef(LongJsonRecord(json));
        Assert.Equal(0xFF, memory.Data[17]);
        AssertOpenSpoolReading(_decoder.Decode("04", memory));
    }

    [Fact]
    public void Decode_NumbersAsStringsAndNumbers()
    {
        var json = "{\"protocol\":\"openspool\",\"min_temp\":190,\"max_temp\":\"210\",\"weight\":\"750\",\"diameter\":\"2.85\"}";
        var r = _decoder.Decode("04", Type2WithNdef(JsonRecord(json)));
        Assert.Equal(190, r.HotendMinC);
        Assert.Equal(210, r.HotendMaxC);
        Assert.Equal(750, r.NetWeightG);
        Assert.Equal(2.85, r.DiameterMm);
    }

    [Fact]
    public void Decode_UnparseableNumbersAreIgnored()
    {
        var json = "{\"protocol\":\"openspool\",\"min_temp\":\"hot\",\"max_temp\":null,\"weight\":true,\"diameter\":{},\"bed_min_temp\":99999999999}";
        var r = _decoder.Decode("04", Type2WithNdef(JsonRecord(json)));
        Assert.Null(r.HotendMinC);
        Assert.Null(r.HotendMaxC);
        Assert.Null(r.NetWeightG);
        Assert.Null(r.DiameterMm);
        Assert.Null(r.BedMinC);
    }

    [Fact]
    public void Decode_ColorWithoutHashIsUppercased()
    {
        var json = "{\"protocol\":\"openspool\",\"color_hex\":\"ff00aa\"}";
        Assert.Equal(["FF00AA"], _decoder.Decode("04", Type2WithNdef(JsonRecord(json))).Colors);
    }

    [Fact]
    public void Decode_MissingFieldsStayNull()
    {
        var r = _decoder.Decode("04", Type2WithNdef(JsonRecord("{\"protocol\":\"openspool\"}")));
        Assert.Null(r.MaterialType);
        Assert.Null(r.Brand);
        Assert.Empty(r.Colors);
        Assert.Null(r.NetWeightG);
    }

    [Fact]
    public void Decode_JsonWithoutOpenSpoolProtocol_Throws()
    {
        var memory = Type2WithNdef(JsonRecord("{\"protocol\":\"other\",\"type\":\"PLA\"}"));
        Assert.Throws<TagNotRecognizedException>(() => _decoder.Decode("04", memory));
    }

    [Fact]
    public void Decode_NonJsonRecord_Throws()
    {
        var memory = Type2WithNdef(JsonRecord("hello", "text/plain"));
        Assert.Throws<TagNotRecognizedException>(() => _decoder.Decode("04", memory));
    }

    [Fact]
    public void Decode_InvalidJson_Throws()
    {
        var memory = Type2WithNdef(JsonRecord("{not json"));
        Assert.Throws<TagNotRecognizedException>(() => _decoder.Decode("04", memory));
    }

    [Fact]
    public void Decode_ChunkedRecord_Throws()
    {
        var record = JsonRecord(OpenSpoolJson);
        record[0] |= 0x20;
        Assert.Throws<TagNotRecognizedException>(() => _decoder.Decode("04", Type2WithNdef(record)));
    }

    [Fact]
    public void Decode_OpenSpoolRecordAfterAnotherRecord()
    {
        var first = JsonRecord("{\"protocol\":\"other\"}");
        first[0] = 0x92; // MB, SR, TNF 2 (no ME)
        var second = JsonRecord(OpenSpoolJson);
        second[0] = 0x52; // ME, SR, TNF 2 (no MB)
        AssertOpenSpoolReading(_decoder.Decode("04", Type2WithNdef([.. first, .. second])));
    }

    [Fact]
    public void Find_SkipsLockAndMemoryControlTlvs()
    {
        var message = JsonRecord(OpenSpoolJson);
        var data = new byte[16 + 5 + 4 + message.Length + 8];
        new byte[] { 0xE1, 0x10, 0x12, 0x00 }.CopyTo(data, 12);
        byte[] prefix = [0x01, 0x03, 0xA0, 0x0C, 0x34, 0x00];
        prefix.CopyTo(data, 16);
        Tlv(message).CopyTo(data, 16 + prefix.Length);
        var found = NdefReader.Find(new TagMemory(TagMemoryKind.Type2, data, null, []));
        Assert.NotNull(found);
        Assert.Single(found.Records);
        Assert.Equal(2, found.Records[0].Tnf);
        Assert.Equal("application/json", found.Records[0].Type);
    }

    [Fact]
    public void Find_EmptyTlv_IsAnEmptyMessage()
    {
        var found = NdefReader.Find(Type2WithNdef([]));
        Assert.NotNull(found);
        Assert.Empty(found.Records);
    }

    [Fact]
    public void Find_ClassicMemory_IsNull()
    {
        var data = new byte[TagMemory.ClassicBytes];
        Assert.Null(NdefReader.Find(new TagMemory(TagMemoryKind.Classic, data, null, [])));
    }

    [Fact]
    public void Find_Type2WithoutCapabilityMagic_IsNull()
    {
        var memory = Type2WithNdef(JsonRecord(OpenSpoolJson));
        memory.Data[12] = 0x00;
        Assert.Null(NdefReader.Find(memory));
        Assert.False(_decoder.CanDecode(memory));
    }

    [Fact]
    public void Find_Type2TooShortForCapabilityContainer_IsNull()
    {
        Assert.Null(NdefReader.Find(new TagMemory(TagMemoryKind.Type2, new byte[8], null, [])));
        Assert.Null(NdefReader.Find(new TagMemory(TagMemoryKind.NfcV, new byte[2], null, [])));
    }

    [Fact]
    public void Find_NoNdefTlv_IsNull()
    {
        var data = new byte[32];
        data[12] = 0xE1;
        data[16] = 0xFE;
        Assert.Null(NdefReader.Find(new TagMemory(TagMemoryKind.Type2, data, null, [])));
    }

    [Fact]
    public void Find_TlvLengthPastTheEnd_IsNull()
    {
        var data = new byte[24];
        data[12] = 0xE1;
        data[16] = 0x03;
        data[17] = 0x50;
        Assert.Null(NdefReader.Find(new TagMemory(TagMemoryKind.Type2, data, null, [])));
    }

    [Fact]
    public void Find_ThreeByteTlvLengthPastTheEnd_IsNull()
    {
        var data = new byte[24];
        data[12] = 0xE1;
        data[16] = 0x03;
        data[17] = 0xFF;
        data[18] = 0x10;
        data[19] = 0x00;
        Assert.Null(NdefReader.Find(new TagMemory(TagMemoryKind.Type2, data, null, [])));
    }

    [Fact]
    public void Find_TlvTagAtTheLastByte_IsNull()
    {
        var data = new byte[17];
        data[12] = 0xE1;
        data[16] = 0x03;
        Assert.Null(NdefReader.Find(new TagMemory(TagMemoryKind.Type2, data, null, [])));
    }

    [Theory]
    [InlineData(1)] // flags only
    [InlineData(2)] // flags + type length
    [InlineData(3)] // the payload length is present, the type and payload are not
    [InlineData(10)]
    public void Find_TruncatedRecord_IsNull(int keep)
    {
        var record = JsonRecord(OpenSpoolJson)[..keep];

        // The TLV length matches the truncated record, so only the record header is malformed.
        Assert.Null(NdefReader.Find(Type2WithNdef(record)));
    }

    [Fact]
    public void Find_TruncatedLongRecordHeader_IsNull()
    {
        // Non-short record whose 4-byte payload length is cut off.
        Assert.Null(NdefReader.Find(Type2WithNdef([0xC2, 0x10, 0x00, 0x00])));
    }

    [Fact]
    public void Find_RecordWithPayloadLengthBeyondTheMessage_IsNull()
    {
        Assert.Null(NdefReader.Find(Type2WithNdef([0xD2, 0x01, 0xFF, (byte)'a', 0x01])));
    }

    [Fact]
    public void Find_IdLengthFlagWithMissingIdLengthByte_IsNull()
    {
        Assert.Null(NdefReader.Find(Type2WithNdef([0xDA, 0x01, 0x00])));
    }

    [Fact]
    public void Find_RecordsWithoutMessageEnd_IsNull()
    {
        var record = JsonRecord(OpenSpoolJson);
        record[0] = 0x92; // no ME
        Assert.Null(NdefReader.Find(Type2WithNdef(record)));
    }

    [Fact]
    public void Redact_ReturnsACopyOfTheMemory()
    {
        var memory = Type2WithNdef(JsonRecord(OpenSpoolJson));
        var copy = _decoder.Redact(memory);
        Assert.Equal(memory.Data, copy);
        Assert.NotSame(memory.Data, copy);
    }
}
