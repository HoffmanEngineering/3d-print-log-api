using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Tag;

/// <summary>The complete set of tags a print should carry.</summary>
public class SetPrintTagsDto : IValidatableObject
{
    /// <summary>
    /// Ids of your own tags. Replaces the print's tags; send an empty list to remove them all.
    /// Required, so a missing or misspelled field is a 400 rather than "remove every tag".
    /// Duplicates are ignored, and the limit of 20 counts distinct ids.
    /// </summary>
    public ICollection<Guid>? TagIds { get; set; }

    // Validation rather than `required`, per the DTO rules: required-ness is enforced here, and
    // the limit is applied after de-duplication, which an attribute on the list cannot do.
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TagIds is null)
        {
            yield return new ValidationResult(
                "tagIds is required. Send an empty list to remove every tag.", [nameof(TagIds)]);
            yield break;
        }

        if (TagIds.Distinct().Count() > Models.Tag.MaxTagsPerPrint)
        {
            yield return new ValidationResult(
                $"A print can have at most {Models.Tag.MaxTagsPerPrint} tags.", [nameof(TagIds)]);
        }
    }
}
