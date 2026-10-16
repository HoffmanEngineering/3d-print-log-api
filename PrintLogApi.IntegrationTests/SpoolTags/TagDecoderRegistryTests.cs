using System.Security.Cryptography;
using PrintLogApi.SpoolTags;
using PrintLogApi.SpoolTags.Decoding;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

public class TagDecoderRegistryTests
{
    private static TagDecoderRegistry Registry(params ITagDecoder[] extra)
    {
        // Registered out of priority order on purpose.
        ITagDecoder[] real = [new NdefOpenSpoolDecoder(), new AnycubicAceDecoder(), new SnapmakerDecoder()];
        return new TagDecoderRegistry(real.Concat(extra));
    }

    [Theory]
    [InlineData("snapmaker-805BEC3E", "snapmaker")]
    [InlineData("snapmaker-50EDB4E2", "snapmaker")]
    [InlineData("anycubic-539D1881020001", "anycubic-ace")]
    [InlineData("anycubic-dng-0416DE5A061F91", "anycubic-ace")]
    public void Fixtures_DecodeWithTheRightFormat(string fixture, string format)
    {
        var result = Registry().Decode("00", TagFixtures.Memory(fixture));

        Assert.Equal(TagDecodeOutcome.Decoded, result.Outcome);
        Assert.Equal(format, result.DetectedFormat);
        Assert.Equal(format, result.Reading!.Format);
        Assert.Null(result.RedactedData);
    }

    [Fact]
    public void BlankTag_IsBlankWithNoData()
    {
        var result = Registry().Decode("00", TagFixtures.Memory("blank-ev1-0416C55A061F91"));

        Assert.Equal(TagDecodeOutcome.Blank, result.Outcome);
        Assert.Equal("openspool", result.DetectedFormat);
        Assert.Null(result.Reading);
        Assert.Null(result.RedactedData);
    }

    [Fact]
    public void RandomClassicBytesWithNoKeyFormat_AreUnknownAndKeepTheInput()
    {
        var data = RandomNumberGenerator.GetBytes(1024);
        var result = Registry().Decode("00", new TagMemory(TagMemoryKind.Classic, data, null, []));

        Assert.Equal(TagDecodeOutcome.Unknown, result.Outcome);
        Assert.Null(result.DetectedFormat);
        Assert.Equal(data, result.RedactedData);
    }

    [Fact]
    public void NullMemory_IsUnknownWithNoData()
    {
        var result = Registry().Decode("00", null);

        Assert.Equal(TagDecodeOutcome.Unknown, result.Outcome);
        Assert.Null(result.RedactedData);
    }

    [Fact]
    public void UnreadableRecordSector_IsPartial()
    {
        var source = TagFixtures.Memory("snapmaker-805BEC3E");
        var result = Registry().Decode("00", source with { Unreadable = [1] });

        Assert.Equal(TagDecodeOutcome.Partial, result.Outcome);
        Assert.Equal("partial-read", result.Error);
        Assert.Null(result.Reading);
        Assert.Null(result.RedactedData);
    }

    [Fact]
    public void UnreadableSignatureSector_StillDecodes()
    {
        var source = TagFixtures.Memory("snapmaker-805BEC3E");
        var result = Registry().Decode("00", source with { Unreadable = [12] });

        Assert.Equal(TagDecodeOutcome.Decoded, result.Outcome);
    }

    [Fact]
    public void ShortAnycubicType2_IsPartial()
    {
        var source = TagFixtures.Memory("anycubic-539D1881020001");
        var result = Registry().Decode("00", source with { Data = source.Data[..96] });

        Assert.Equal(TagDecodeOutcome.Partial, result.Outcome);
    }

    [Fact]
    public void ThrowingDecoder_IsFailedWithItsOwnRedaction()
    {
        var memory = new TagMemory(TagMemoryKind.Classic, [1, 2, 3, 4], "throwing", []);
        var result = Registry(new ThrowingDecoder()).Decode("00", memory);

        Assert.Equal(TagDecodeOutcome.Failed, result.Outcome);
        Assert.Equal("throwing", result.DetectedFormat);
        Assert.Equal(new byte[4], result.RedactedData);
        Assert.StartsWith("InvalidOperationException: ", result.Error);
        Assert.Equal(500, result.Error!.Length);
    }

    [Fact]
    public void UnknownFormatDecoder_RunsAfterTheKnownOnes()
    {
        // The test decoder claims everything; the Snapmaker decoder must still win.
        var result = Registry(new ThrowingDecoder(canDecodeAll: true)).Decode("00", TagFixtures.Memory("snapmaker-805BEC3E"));

        Assert.Equal(TagDecodeOutcome.Decoded, result.Outcome);
        Assert.Equal("snapmaker", result.DetectedFormat);
    }

    [Fact]
    public void DecodedReading_IsSanitized()
    {
        var clean = Registry().Decode("00", TagFixtures.Memory("snapmaker-805BEC3E"));
        var patched = new TagDecoderRegistry([new PatchingDecoder(new SnapmakerDecoder())])
            .Decode("00", TagFixtures.Memory("snapmaker-805BEC3E"));

        Assert.DoesNotContain("hotend-out-of-range", clean.Reading!.Warnings);
        Assert.Contains("hotend-out-of-range", patched.Reading!.Warnings);
        Assert.Null(patched.Reading.HotendMaxC);
    }

    [Fact]
    public void DecoderWarnings_SurviveSanitizing()
    {
        var result = new TagDecoderRegistry([new PatchingDecoder(new SnapmakerDecoder(), "from-decoder")])
            .Decode("00", TagFixtures.Memory("snapmaker-805BEC3E"));

        Assert.Contains("from-decoder", result.Reading!.Warnings);
    }

    [Fact]
    public void CanDecodeThatThrows_IsFailedAndStopsThere()
    {
        var stub = new StubDecoder("snapmaker") { ProbeThrows = true };
        var result = new TagDecoderRegistry([stub, new AnycubicAceDecoder()])
            .Decode("00", TagFixtures.Memory("anycubic-539D1881020001"));

        Assert.Equal(TagDecodeOutcome.Failed, result.Outcome);
        Assert.Equal("snapmaker", result.DetectedFormat);
        Assert.StartsWith("InvalidOperationException: ", result.Error);
        Assert.Equal(new byte[] { 9, 9 }, result.RedactedData);
    }

    [Fact]
    public void DecodeAndRedactBothThrow_IsFailedWithNoData()
    {
        var stub = new StubDecoder("snapmaker") { DecodeThrows = true, RedactThrows = true };
        var result = new TagDecoderRegistry([stub]).Decode("00", TagFixtures.Memory("snapmaker-805BEC3E"));

        Assert.Equal(TagDecodeOutcome.Failed, result.Outcome);
        Assert.Equal("snapmaker", result.DetectedFormat);
        Assert.Null(result.RedactedData);
    }

    [Fact]
    public void NotRecognized_IsUnknownWithTheDecoderRedaction()
    {
        var stub = new StubDecoder("snapmaker") { NotRecognized = true };
        var result = new TagDecoderRegistry([stub]).Decode("00", TagFixtures.Memory("snapmaker-805BEC3E"));

        Assert.Equal(TagDecodeOutcome.Unknown, result.Outcome);
        Assert.Null(result.DetectedFormat);
        Assert.Equal(new byte[] { 9, 9 }, result.RedactedData);
    }

    [Fact]
    public void ThrowOnDecode_DoesNotFallThroughToTheNextDecoder()
    {
        var stub = new StubDecoder("snapmaker") { DecodeThrows = true };
        var result = new TagDecoderRegistry([stub, new SnapmakerDecoder()])
            .Decode("00", TagFixtures.Memory("snapmaker-805BEC3E"));

        Assert.Equal(TagDecodeOutcome.Failed, result.Outcome);
        Assert.Equal("snapmaker", result.DetectedFormat);
        Assert.Equal(new byte[] { 9, 9 }, result.RedactedData);
    }

    [Fact]
    public void LongErrorIsNotCutInsideASurrogatePair()
    {
        // "InvalidOperationException: " is 27 chars; the emoji pair then straddles index 499/500.
        var message = new string('x', 499 - 27) + "😀" + "tail";
        var stub = new StubDecoder("snapmaker") { DecodeThrows = true, Message = message };
        var result = new TagDecoderRegistry([stub]).Decode("00", TagFixtures.Memory("snapmaker-805BEC3E"));

        Assert.Equal(499, result.Error!.Length);
        Assert.False(char.IsHighSurrogate(result.Error[^1]));
    }

    private sealed class StubDecoder(string format) : ITagDecoder
    {
        public bool ProbeThrows { get; init; }
        public bool DecodeThrows { get; init; }
        public bool RedactThrows { get; init; }
        public bool NotRecognized { get; init; }
        public string Message { get; init; } = "boom";

        public string Format => format;

        public int Version => 1;

        public bool CanDecode(TagMemory memory)
        {
            return ProbeThrows ? throw new InvalidOperationException(Message) : true;
        }

        public TagReading Decode(string uid, TagMemory memory)
        {
            if (NotRecognized)
            {
                throw new TagNotRecognizedException();
            }

            return DecodeThrows ? throw new InvalidOperationException(Message) : new TagReading();
        }

        public byte[] Redact(TagMemory memory)
        {
            return RedactThrows ? throw new InvalidOperationException("no") : [9, 9];
        }
    }

    private sealed class ThrowingDecoder(bool canDecodeAll = false) : ITagDecoder
    {
        public string Format => "throwing";

        public int Version => 1;

        public bool CanDecode(TagMemory memory)
        {
            return canDecodeAll || memory.KeyFormat == "throwing";
        }

        public TagReading Decode(string uid, TagMemory memory)
        {
            throw new InvalidOperationException(new string('x', 800));
        }

        public byte[] Redact(TagMemory memory)
        {
            return new byte[memory.Data.Length];
        }
    }

    private sealed class PatchingDecoder(SnapmakerDecoder inner, string? warning = null) : ITagDecoder
    {
        public string Format => inner.Format;

        public int Version => inner.Version;

        public bool CanDecode(TagMemory memory) => inner.CanDecode(memory);

        public byte[] Redact(TagMemory memory) => inner.Redact(memory);

        public TagReading Decode(string uid, TagMemory memory)
        {
            var reading = inner.Decode(uid, memory);
            reading.HotendMaxC = 600;
            if (warning is not null)
            {
                reading.Warnings.Add(warning);
            }

            return reading;
        }
    }
}
