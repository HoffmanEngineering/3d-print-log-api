using System;
using System.Linq;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Profiles
{
    public class PrintProfile : Profile
    {
        public PrintProfile()
        {
            CreateMap<AddPrintDTO, Print>()
                .ForMember(dest => dest.ProjectId, opt => opt.Ignore());

            CreateMap<Print, PrintSummaryDTO>()
                .ForMember(dest => dest.DefaultPrintImageId, opt => opt.MapFrom(src => src.Images!
                                                                        .Where(i => i.IsDefault == true)
                                                                        .Select(i => i.Id)
                                                                        .FirstOrDefault()))
                .ForMember(dest => dest.CreatedByUserId, opt => opt.MapFrom(src => src.CreatedById))
                .ForMember(dest => dest.CommentCount, opt => opt.MapFrom(src => src.Comments!.Select(c => c.Comment).Count()))
                .ForMember(dest => dest.SumActualFilamentWeightMg, opt => opt.MapFrom(src => src.FilamentUsage!.Sum(p => p.AmountMg.HasValue &&
                                                                                                                    p.AmountMg > 0 ?
                                                                                                                    p.AmountMg : 0)))
                .ForMember(dest => dest.SumEstimatedFilamentWeightMg, opt => opt.MapFrom(src => src.FilamentUsage!.Sum(p => p.EstimatedAmountMg.HasValue &&
                                                                                                                    p.EstimatedAmountMg > 0 ?
                                                                                                                    p.EstimatedAmountMg : 0)))
                .ForMember(dest => dest.TotalFilamentWeightMg, opt => opt.MapFrom(src => src.FilamentUsage!.Sum(p => p.AmountMg.HasValue &&
                                                                                                                    p.AmountMg > 0 ?
                                                                                                                    p.AmountMg :
                                                                                                                    p.EstimatedAmountMg.HasValue &&
                                                                                                                    p.EstimatedAmountMg > 0 ?
                                                                                                                    p.EstimatedAmountMg : 0)))
                .ForMember(dest => dest.ProjectId,
                    opt => opt.MapFrom(src => src.ProjectId))
                .ForMember(dest => dest.ProjectName,
                    opt => opt.MapFrom(src => src.Project != null ? src.Project.Name : null));

            CreateMap<Print, PrintFeedSummaryDto>()
                .ForMember(dest => dest.DefaultPrintImageId, opt => opt.MapFrom(src => src.Images!
                                                                        .Where(i => i.IsDefault == true)
                                                                        .Select(i => i.Id)
                                                                        .FirstOrDefault()))
                .ForMember(dest => dest.CreatedBy, opt => opt.MapFrom(src => src.CreatedBy))
                .ForMember(dest => dest.CreatedDate, opt => opt.MapFrom(src => (DateTimeOffset) DateTime.SpecifyKind(src.CreatedDate, DateTimeKind.Utc)))
                .ForMember(dest => dest.CommentCount, opt => opt.MapFrom(src => src.Comments!.Select(c => c.Comment).Count()));

            CreateMap<Print, PrintDetailDTO>()
                .ForMember(dest => dest.PrinterId, opt => opt.MapFrom(src => src.Printer.Id))
                .ForMember(dest => dest.Images, opt => opt.MapFrom(src => src.Images!.OrderBy(i => i.DisplayOrder)))
                .ForMember(dest => dest.CreatedByUserId, opt => opt.MapFrom(src => src.CreatedById))
                .ForMember(dest => dest.Comments, opt => opt.MapFrom(src => src.Comments!.Select(c => c.Comment)))
                .ForMember(dest => dest.ProjectId, opt => opt.MapFrom(src => src.ProjectId));

            CreateMap<PrintDetailDTO, Print>()
                .ForMember(dest => dest.Printer, opt => opt.Ignore())
                .ForMember(dest => dest.Images, opt => opt.Ignore())
                .ForMember(dest => dest.Comments, opt => opt.Ignore());

            CreateMap<PutPrintDetailDto, Print>()
                .ForMember(dest => dest.ProjectId, opt => opt.Ignore());

            // The rows and the scalar ADD: the scalar is "other filament", material never
            // attached to a tracked spool (see PrintMetrics.MaterialMgExpr). The actual and
            // estimated columns stay SEPARATE — consumers resolve between them — so neither
            // falls back to the other here. Each term is guarded: zero or less means "not
            // recorded", so a corrupt negative contributes 0 instead of subtracting.
            // ProjectTo needs its own inline copy; PrintProfileMaterialTests pins it.
            CreateMap<Print, PrintStatistic>()
                .ForMember(dest => dest.EstimatedFilamentUsageMg, opt => opt.MapFrom(src =>
                    src.FilamentUsage!.Sum(p => p.EstimatedAmountMg.HasValue && p.EstimatedAmountMg > 0
                            ? p.EstimatedAmountMg.Value : 0)
                        + (src.EstimatedFilamentUsageMg.HasValue && src.EstimatedFilamentUsageMg > 0
                            ? src.EstimatedFilamentUsageMg.Value : 0)))
                .ForMember(dest => dest.FilamentUsageMg, opt => opt.MapFrom(src =>
                    src.FilamentUsage!.Sum(p => p.AmountMg.HasValue && p.AmountMg > 0
                            ? p.AmountMg.Value : 0)
                        + (src.FilamentUsageMg.HasValue && src.FilamentUsageMg > 0
                            ? src.FilamentUsageMg.Value : 0)));

            CreateMap<Print, PrintDetailReport>()
                .ForMember(dest => dest.PrinterName, opt => opt.MapFrom(src => src.Printer.Name))
                .ForMember(dest => dest.PrinterMake, opt => opt.MapFrom(src => src.Printer.Make))
                .ForMember(dest => dest.PrinterModel, opt => opt.MapFrom(src => src.Printer.Model))
                // Same guarded rows + guarded "other filament" scalar as PrintStatistic above, so
                // the CSV export and the stats screen cannot disagree. PrintProfileMaterialTests
                // pins both copies.
                .ForMember(dest => dest.EstimatedFilamentUsageG, opt => opt.MapFrom(src =>
                    (src.FilamentUsage!.Sum(p => p.EstimatedAmountMg.HasValue && p.EstimatedAmountMg > 0
                            ? p.EstimatedAmountMg.Value : 0)
                        + (src.EstimatedFilamentUsageMg.HasValue && src.EstimatedFilamentUsageMg > 0
                            ? src.EstimatedFilamentUsageMg.Value : 0)) / 1000.0))
                .ForMember(dest => dest.FilamentUsageG, opt => opt.MapFrom(src =>
                    (src.FilamentUsage!.Sum(p => p.AmountMg.HasValue && p.AmountMg > 0
                            ? p.AmountMg.Value : 0)
                        + (src.FilamentUsageMg.HasValue && src.FilamentUsageMg > 0
                            ? src.FilamentUsageMg.Value : 0)) / 1000.0))
                // Combine the FilamentTypes with the display names of the filaments, deliminated by ;.
                .ForMember(dest => dest.FilamentType, opt => opt.MapFrom(src => (src.FilamentType + "; " + string.Join("; ", src.FilamentUsage!.Select(f => f.Filament!.DisplayName).ToList())).Trim().Trim(';').Trim()));

            CreateMap<PrintFilament, PrintFilamentSummaryDto>()
                .ForMember(dest => dest.Filament, opt => opt.MapFrom(src => src.Filament))
                .ForMember(dest => dest.IsEstimatedLengthSource, opt => opt.MapFrom(src => src.EstimatedSource == PrintFilament.SourceMeasurement.Length))
                .ForMember(dest => dest.IsActualLengthSource, opt => opt.MapFrom(src => src.Source == PrintFilament.SourceMeasurement.Length));

            CreateMap<PrintFilamentSummaryDto, PrintFilament>()
                .ForMember(dest => dest.Filament, opt => opt.Ignore())
                .ForMember(dest => dest.EstimatedSource, opt => opt.MapFrom(src => src.EstimatedSource.HasValue ? src.EstimatedSource : src.IsEstimatedLengthSource ? PrintFilament.SourceMeasurement.Length : PrintFilament.SourceMeasurement.Weight))
                .ForMember(dest => dest.Source, opt => opt.MapFrom(src => src.Source.HasValue ? src.Source : src.IsActualLengthSource ? PrintFilament.SourceMeasurement.Length : PrintFilament.SourceMeasurement.Weight));

            CreateMap<PutPrintFilamentSummaryDto, PrintFilament>()
                .ForMember(dest => dest.EstimatedSource, opt => opt.MapFrom(src => src.EstimatedSource.HasValue ? src.EstimatedSource : src.IsEstimatedLengthSource ? PrintFilament.SourceMeasurement.Length : PrintFilament.SourceMeasurement.Weight))
                .ForMember(dest => dest.Source, opt => opt.MapFrom(src => src.Source.HasValue ? src.Source : src.IsActualLengthSource ? PrintFilament.SourceMeasurement.Length : PrintFilament.SourceMeasurement.Weight));

        }
    }

}
