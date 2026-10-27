using AutoMapper;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.SpoolTags;
using PrintLogApi.Services;
using PrintLogApi.SpoolTags.Decoding;
using PrintLogApi.SpoolTags.Matching;

namespace PrintLogApi.SpoolTags;

/// <summary>Resolves one raw tag read: to the user's spool when the tag is linked, otherwise to a decoded reading with matches.</summary>
public class TagReadService(
    PrintLogContext db,
    TagDecoderRegistry registry,
    FilamentCatalogMatcher matcher,
    TagSuggestionRanker ranker,
    IFilamentService filamentService,
    IMapper mapper)
{
    private const int MaxTechsLength = 200;
    private const string HoldStillHint = "hold-still";

    /// <exception cref="BadRequestException">The UID or the memory is invalid.</exception>
    public async Task<TagReadResponse> ResolveAsync(long userId, TagReadRequest request, CancellationToken ct)
    {
        var uid = TagUid.Normalize(request.Uid) ?? throw new BadRequestException("The tag UID must be hex bytes.");
        var memory = TagMemory.Parse(request.Memory);

        var tag = await db.FilamentTags.SingleOrDefaultAsync(t => t.UserId == userId && t.Uid == uid, ct);
        if (tag is not null)
        {
            return await ResolveLinkedAsync(tag, uid, memory, ct);
        }

        var result = registry.Decode(uid, memory);
        if (result.Outcome is TagDecodeOutcome.Unknown or TagDecodeOutcome.Failed && result.RedactedData is not null && memory is not null)
        {
            db.TagReadFailures.Add(new TagReadFailure
            {
                CreatedAt = DateTime.UtcNow,
                Uid = uid,
                Techs = JoinTechs(request.Techs),
                MemoryKind = memory.Kind.ToString().ToLowerInvariant(),
                Data = result.RedactedData,
                DetectedFormat = result.DetectedFormat,
                Error = result.Error ?? "unrecognized",
            });
            await db.SaveChangesAsync(ct);
        }

        var reading = result.Outcome == TagDecodeOutcome.Decoded ? result.Reading : null;
        var match = reading is null ? null : await matcher.MatchAsync(reading, ct);
        var suggestions = await ranker.RankAsync(userId, reading, ct);

        return new TagReadResponse
        {
            Status = "unknown",
            Reading = reading,
            CatalogMatch = CatalogMatchDto.From(match),
            Suggestions = suggestions
                .Select(s => new TagSuggestionDto { FilamentId = s.FilamentId, Score = s.Score, Reasons = s.Reasons.ToList() })
                .ToList(),
            Hint = result.Outcome == TagDecodeOutcome.Partial ? HoldStillHint : null,
        };
    }

    private async Task<TagReadResponse> ResolveLinkedAsync(FilamentTag tag, string uid, TagMemory? memory, CancellationToken ct)
    {
        if (memory is not null)
        {
            var result = registry.Decode(uid, memory);
            if (result is { Outcome: TagDecodeOutcome.Decoded, Reading: { } reading })
            {
                tag.Format = result.DetectedFormat ?? reading.Format;
                tag.DecoderVersion = reading.DecoderVersion;

                // Over the size cap: keep the previous reading rather than lose the tag's last good one.
                if (TagReadingJson.Serialize(reading) is { } json)
                {
                    tag.DecodedJson = json;
                }
            }
        }

        tag.LastReadAt = DateTime.UtcNow;

        // Only the tag row is tracked as changed, so the filament's UpdatedDate stays as it was.
        await db.SaveChangesAsync(ct);

        var filament = await filamentService.GetFilamentById(tag.FilamentId);
        FilamentDetailDto? dto = null;
        if (filament is not null)
        {
            dto = mapper.Map<FilamentDetailDto>(filament);
            await filamentService.HydrateDetailImageUrlsAsync(dto, ct);
        }

        return new TagReadResponse { Status = "linked", Filament = dto };
    }

    private static string? JoinTechs(List<string>? techs)
    {
        if (techs is not { Count: > 0 })
        {
            return null;
        }

        var joined = string.Join(',', techs);
        return joined.Length > MaxTechsLength ? joined[..MaxTechsLength] : joined;
    }
}
