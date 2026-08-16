using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Project;

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
                    .FirstOrDefault()));

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
                opt => opt.MapFrom(src => src.Images!.OrderBy(i => i.DisplayOrder)));

        CreateMap<ProjectImage, ProjectImageDto>()
            .ForMember(dest => dest.Url, opt => opt.Ignore()); // URL resolved at request time
    }
}
