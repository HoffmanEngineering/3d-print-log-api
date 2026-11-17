using PrintLogApi.Exceptions;
using PrintLogApi.Models.DTOs.SpoolTags;

namespace PrintLogApi.SpoolTags;

public enum TagMemoryKind
{
    Classic,
    Type2,
    NfcV,
}

/// <summary>A validated raw tag memory dump.</summary>
public sealed record TagMemory(TagMemoryKind Kind, byte[] Data, string? KeyFormat, IReadOnlyList<int> Unreadable)
{
    public const int ClassicBytes = 1024;
    public const int Type2MaxBytes = 1024;
    public const int NfcVMaxBytes = 2048;

    /// <summary>Null for a null DTO. Throws <see cref="BadRequestException"/> for an unknown kind, bad base64 or a size outside the limits.</summary>
    public static TagMemory? Parse(TagMemoryDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        var kind = dto.Kind?.Trim().ToLowerInvariant() switch
        {
            "classic" => TagMemoryKind.Classic,
            "type2" => TagMemoryKind.Type2,
            "nfcv" => TagMemoryKind.NfcV,
            _ => throw new BadRequestException("Unknown tag memory kind."),
        };

        if (string.IsNullOrEmpty(dto.Data))
        {
            throw new BadRequestException("Tag memory data is required.");
        }

        // Base64 inflates by a third, so a string over this length cannot decode within the limits.
        if (dto.Data.Length > NfcVMaxBytes * 4 / 3 + 8)
        {
            throw new BadRequestException("Tag memory is too large.");
        }

        var buffer = new byte[NfcVMaxBytes + 3];
        if (!Convert.TryFromBase64String(dto.Data, buffer, out var written))
        {
            throw new BadRequestException("Tag memory is not valid base64.");
        }

        var ok = kind switch
        {
            TagMemoryKind.Classic => written == ClassicBytes,
            TagMemoryKind.Type2 => written is > 0 and <= Type2MaxBytes,
            _ => written is > 0 and <= NfcVMaxBytes,
        };
        if (!ok)
        {
            throw new BadRequestException("Tag memory has an unsupported size for its kind.");
        }

        return new TagMemory(kind, buffer[..written], dto.KeyFormat, dto.Unreadable ?? []);
    }
}
