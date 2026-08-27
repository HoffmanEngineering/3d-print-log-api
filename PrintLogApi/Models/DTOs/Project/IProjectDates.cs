namespace PrintLogApi.Models.DTOs.Project;

/// <summary>
/// The project date fields shared by every read DTO, so a single typed AutoMapper
/// AfterMap can populate them for both the detail and summary maps.
/// </summary>
/// <remarks>
/// <see cref="StartDate"/> and <see cref="FinishDate"/> are RESOLVED values — what the
/// UI displays. The two override properties are the RAW stored values — what the edit
/// form binds to. Both are returned so the client can tell "pinned by hand" apart from
/// "derived from prints" without a second request.
/// </remarks>
public interface IProjectDates
{
    /// <summary>Resolved start date. Never null: falls back to the project's creation date.</summary>
    DateOnly StartDate { get; set; }

    /// <summary>Resolved finish date. Null when the project has no print with a start date.</summary>
    DateOnly? FinishDate { get; set; }

    /// <summary>Raw manual start override. Null means the start date is automatic.</summary>
    DateOnly? StartDateOverride { get; set; }

    /// <summary>Raw manual finish override. Null means the finish date is automatic.</summary>
    DateOnly? FinishDateOverride { get; set; }
}
