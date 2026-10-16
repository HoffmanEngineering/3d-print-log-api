namespace PrintLogApi.SpoolTags.Decoding;

/// <summary>Turns one tag format's raw memory into a <see cref="TagReading"/>.</summary>
public interface ITagDecoder
{
    string Format { get; }

    int Version { get; }

    bool CanDecode(TagMemory memory);

    /// <summary>Raw output: the registry sanitizes it. May throw on malformed data; the registry catches.</summary>
    TagReading Decode(string uid, TagMemory memory);

    /// <summary>A copy of the memory with everything that must not be stored zeroed.</summary>
    byte[] Redact(TagMemory memory);
}
