using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Project;
using PrintLogApi.Services;

namespace PrintLogApi.Profiles;

public class ProjectProfile : Profile
{
    public ProjectProfile()
    {
        CreateMap<AddProjectDto, Project>();

        CreateMap<PutProjectDto, Project>();

        CreateMap<Project, ProjectSummaryDto>()
            .ForMember(dest => dest.CreatedDate,
                opt => opt.MapFrom(src => (DateTimeOffset)DateTime.SpecifyKind(src.CreatedDate, DateTimeKind.Utc)))
            .ForMember(dest => dest.PrintCount,
                opt => opt.MapFrom(src => src.Prints!.Count()))
            // `?? 0` had NO fallback at all: a never-completed print contributed 0 even when it
            // carried a real slicer estimate. Same defect the MCP summary had.
            .ForMember(dest => dest.TotalPrintTimeInSeconds,
                opt => opt.MapFrom(src => src.Prints!.Sum(p =>
                    p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0 ? p.PrintTimeInSeconds.Value
                    : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0 ? p.EstimatedPrintTimeInSeconds.Value
                    : 0)))
            // Deliberately estimate-only — a DIFFERENT question ("what did the slicer predict?").
            // Do not turn this into a resolved value. Guarded > 0 only.
            .ForMember(dest => dest.TotalEstimatedPrintTimeInSeconds,
                opt => opt.MapFrom(src => src.Prints!.Sum(p =>
                    p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0
                        ? p.EstimatedPrintTimeInSeconds.Value
                        : 0)))
            .ForMember(dest => dest.TotalFilamentWeightMg,
                opt => opt.MapFrom(src => src.Prints!
                    .SelectMany(p => p.FilamentUsage!)
                    .Sum(pf => pf.AmountMg.HasValue && pf.AmountMg > 0
                        ? (long)pf.AmountMg.Value
                        : pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0
                            ? (long)pf.EstimatedAmountMg.Value : 0L)))
            .ForMember(dest => dest.DefaultImageId,
                opt => opt.MapFrom(src => src.Images!
                    .Where(i => i.IsDefault)
                    .Select(i => i.Id)
                    .FirstOrDefault()))
            // Resolved in AfterMap, not MapFrom: the rules live in ProjectDateResolver and
            // must stay identical across every read path.
            .ForMember(dest => dest.StartDate, opt => opt.Ignore())
            .ForMember(dest => dest.FinishDate, opt => opt.Ignore())
            .AfterMap((src, dest) => ResolveDates(src, dest));

        CreateMap<Project, ProjectDetailDto>()
            .ForMember(dest => dest.CreatedDate,
                opt => opt.MapFrom(src => (DateTimeOffset)DateTime.SpecifyKind(src.CreatedDate, DateTimeKind.Utc)))
            .ForMember(dest => dest.CreatedByUserId,
                opt => opt.MapFrom(src => src.CreatedById))
            .ForMember(dest => dest.PrintCount,
                opt => opt.MapFrom(src => src.Prints!.Count()))
            // `?? 0` had NO fallback at all: a never-completed print contributed 0 even when it
            // carried a real slicer estimate. Same defect the MCP summary had.
            .ForMember(dest => dest.TotalPrintTimeInSeconds,
                opt => opt.MapFrom(src => src.Prints!.Sum(p =>
                    p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0 ? p.PrintTimeInSeconds.Value
                    : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0 ? p.EstimatedPrintTimeInSeconds.Value
                    : 0)))
            // Deliberately estimate-only — a DIFFERENT question ("what did the slicer predict?").
            // Do not turn this into a resolved value. Guarded > 0 only.
            .ForMember(dest => dest.TotalEstimatedPrintTimeInSeconds,
                opt => opt.MapFrom(src => src.Prints!.Sum(p =>
                    p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0
                        ? p.EstimatedPrintTimeInSeconds.Value
                        : 0)))
            .ForMember(dest => dest.TotalFilamentWeightMg,
                opt => opt.MapFrom(src => src.Prints!
                    .SelectMany(p => p.FilamentUsage!)
                    .Sum(pf => pf.AmountMg.HasValue && pf.AmountMg > 0
                        ? (long)pf.AmountMg.Value
                        : pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0
                            ? (long)pf.EstimatedAmountMg.Value : 0L)))
            .ForMember(dest => dest.Images,
                opt => opt.MapFrom(src => src.Images!.OrderBy(i => i.DisplayOrder)))
            // Resolved in AfterMap, not MapFrom: the rules live in ProjectDateResolver and
            // must stay identical across every read path.
            .ForMember(dest => dest.StartDate, opt => opt.Ignore())
            .ForMember(dest => dest.FinishDate, opt => opt.Ignore())
            .AfterMap((src, dest) => ResolveDates(src, dest));

        CreateMap<ProjectImage, ProjectImageDto>()
            .ForMember(dest => dest.Url, opt => opt.Ignore()); // URL resolved at request time
    }

    /// <summary>
    /// The single resolution site for all four REST project read paths. Every project path
    /// materializes the entity before mapping (there is no ProjectTo on projects), so calling
    /// into ProjectDateResolver here runs in memory and never reaches a database provider.
    /// </summary>
    /// <remarks>
    /// A null Prints collection means the caller did not Include them. That resolves to the
    /// creation-date fallback rather than throwing, which is correct for paths that legitimately
    /// have no prints loaded.
    /// </remarks>
    private static void ResolveDates(Models.Project src, IProjectDates dest)
    {
        var prints = src.Prints?.Select(p => new ProjectDateResolver.PrintDates(
                p.StartDate, p.PrintTimeInSeconds, p.EstimatedPrintTimeInSeconds))
            ?? Enumerable.Empty<ProjectDateResolver.PrintDates>();

        var (start, finish) = ProjectDateResolver.Resolve(
            src.StartDateOverride, src.FinishDateOverride, src.CreatedDate, prints);

        dest.StartDate = start;
        dest.FinishDate = finish;
        dest.StartDateOverride = src.StartDateOverride;
        dest.FinishDateOverride = src.FinishDateOverride;
    }
}
