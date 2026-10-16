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
    private const int ClassicSectors = 16;
    private const int ClassicSignatureStart = 640;
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

        if (memory.Kind == TagMemoryKind.Classic && NothingRead(memory))
        {
            // No scheme authenticated any sector: there is nothing to decode and nothing worth storing.
            return new TagDecodeResult(TagDecodeOutcome.Unknown, null, null, null, null);
        }

        if (IsPartial(memory))
        {
            return new TagDecodeResult(TagDecodeOutcome.Partial, null, null, PartialReadWarning, null);
        }

        foreach (var candidate in _decoders)
        {
            try
            {
                if (!candidate.CanDecode(memory))
                {
                    continue;
                }

                return Run(candidate, uid, memory);
            }
            catch (Exception ex)
            {
                return Failed(candidate, memory, ex);
            }
        }

        // No decoder claimed it. If the app named a format whose decoder is registered, that decoder
        // knows what to strip; otherwise keep a copy (Classic loses its signature region below).
        var named = _decoders.FirstOrDefault(d => memory.KeyFormat is not null
            && string.Equals(SafeFormat(d), memory.KeyFormat, StringComparison.OrdinalIgnoreCase));
        var data = named is not null ? SafeRedact(named, memory) : (byte[])memory.Data.Clone();
        return new TagDecodeResult(TagDecodeOutcome.Unknown, null, null, null, Backstop(memory, data));
    }

    private static TagDecodeResult Run(ITagDecoder decoder, string uid, TagMemory memory)
    {
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
            return new TagDecodeResult(TagDecodeOutcome.Unknown, null, null, null, Backstop(memory, SafeRedact(decoder, memory)));
        }
        catch (Exception ex)
        {
            return Failed(decoder, memory, ex);
        }
    }

    private static TagDecodeResult Failed(ITagDecoder decoder, TagMemory memory, Exception ex)
    {
        var error = $"{ex.GetType().Name}: {ex.Message}";
        if (error.Length > MaxErrorLength)
        {
            // Do not split a surrogate pair.
            var cut = char.IsHighSurrogate(error[MaxErrorLength - 1]) ? MaxErrorLength - 1 : MaxErrorLength;
            error = error[..cut];
        }

        return new TagDecodeResult(TagDecodeOutcome.Failed, null, SafeFormat(decoder), error, Backstop(memory, SafeRedact(decoder, memory)));
    }

    private static string? SafeFormat(ITagDecoder decoder)
    {
        try
        {
            return decoder.Format;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A decoder that cannot redact its own data gets nothing stored: never keep unredacted bytes.
    private static byte[]? SafeRedact(ITagDecoder decoder, TagMemory memory)
    {
        try
        {
            return decoder.Redact(memory);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Whatever a decoder redacted, a stored Classic dump never keeps the signature sectors (10-15):
    // together with a UID they would make a clone-ready tag.
    private static byte[]? Backstop(TagMemory memory, byte[]? data)
    {
        if (data is not null && memory.Kind == TagMemoryKind.Classic && data.Length > ClassicSignatureStart)
        {
            Array.Clear(data, ClassicSignatureStart, data.Length - ClassicSignatureStart);
        }

        return data;
    }

    private static bool NothingRead(TagMemory memory)
    {
        return memory.Unreadable.Where(s => s is >= 0 and < ClassicSectors).Distinct().Count() >= ClassicSectors;
    }

    private static bool IsPartial(TagMemory memory)
    {
        return memory.Kind switch
        {
            TagMemoryKind.Classic => !string.IsNullOrEmpty(memory.KeyFormat) && memory.Unreadable.Any(s => s is >= 0 and < ClassicRecordSectors),
            TagMemoryKind.Type2 => memory.Data.Length < Type2RecordBytes
                && memory.Data.Length >= Type2MagicOffset + AnycubicMagic.Length
                && memory.Data.AsSpan(Type2MagicOffset, AnycubicMagic.Length).SequenceEqual(AnycubicMagic),
            _ => false,
        };
    }
}
