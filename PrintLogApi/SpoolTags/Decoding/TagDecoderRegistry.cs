namespace PrintLogApi.SpoolTags.Decoding;

public enum TagDecodeOutcome
{
    Decoded,
    Blank,
    Partial,
    Unknown,
    Failed,
}

/// <summary>
/// What the registry made of one read. <c>Reading</c> is set only for <c>Decoded</c>;
/// <c>RedactedData</c> only for <c>Unknown</c> and <c>Failed</c>. <c>Error</c> carries the failure
/// message for <c>Failed</c> and the <c>partial-read</c> warning for <c>Partial</c>.
/// </summary>
public sealed record TagDecodeResult(
    TagDecodeOutcome Outcome,
    TagReading? Reading,
    string? DetectedFormat,
    string? Error,
    byte[]? RedactedData);

/// <summary>Picks the decoder whose format matches the content of a tag and runs it. Never throws for bad tag data.</summary>
public sealed class TagDecoderRegistry
{
    public const string PartialReadWarning = "partial-read";

    private const int MaxErrorLength = 500;
    private const int ClassicRecordSectors = 3;
    private const int Type2RecordBytes = 128;
    private const int Type2MagicOffset = 16;

    private static readonly byte[] AnycubicMagic = [0x7B, 0x00, 0x65, 0x00];

    // Decoders whose format is not listed go last, in registration order.
    private static readonly Dictionary<string, int> Priority = new()
    {
        ["snapmaker"] = 0,
        ["anycubic-ace"] = 1,
        ["openspool"] = 2,
    };

    private readonly ITagDecoder[] _decoders;

    public TagDecoderRegistry(IEnumerable<ITagDecoder> decoders)
    {
        // OrderBy is stable, so unknown formats keep their DI order.
        _decoders = decoders.OrderBy(d => Priority.GetValueOrDefault(d.Format, int.MaxValue)).ToArray();
    }

    public TagDecodeResult Decode(string uid, TagMemory? memory)
    {
        if (memory is null)
        {
            return new TagDecodeResult(TagDecodeOutcome.Unknown, null, null, null, null);
        }

        if (IsPartial(memory))
        {
            return new TagDecodeResult(TagDecodeOutcome.Partial, null, null, PartialReadWarning, null);
        }

        var decoder = _decoders.FirstOrDefault(d => d.CanDecode(memory));
        if (decoder is null)
        {
            return new TagDecodeResult(TagDecodeOutcome.Unknown, null, null, null, (byte[])memory.Data.Clone());
        }

        try
        {
            var reading = decoder.Decode(uid, memory);
            return new TagDecodeResult(TagDecodeOutcome.Decoded, TagReadingSanitizer.Sanitize(reading), decoder.Format, null, null);
        }
        catch (BlankTagException)
        {
            return new TagDecodeResult(TagDecodeOutcome.Blank, null, decoder.Format, null, null);
        }
        catch (TagNotRecognizedException)
        {
            return new TagDecodeResult(TagDecodeOutcome.Unknown, null, null, null, decoder.Redact(memory));
        }
        catch (Exception ex)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            if (error.Length > MaxErrorLength)
            {
                error = error[..MaxErrorLength];
            }

            return new TagDecodeResult(TagDecodeOutcome.Failed, null, decoder.Format, error, decoder.Redact(memory));
        }
    }

    private static bool IsPartial(TagMemory memory)
    {
        return memory.Kind switch
        {
            TagMemoryKind.Classic => memory.Unreadable.Any(s => s is >= 0 and < ClassicRecordSectors),
            TagMemoryKind.Type2 => memory.Data.Length < Type2RecordBytes
                && memory.Data.Length >= Type2MagicOffset + AnycubicMagic.Length
                && memory.Data.AsSpan(Type2MagicOffset, AnycubicMagic.Length).SequenceEqual(AnycubicMagic),
            _ => false,
        };
    }
}
