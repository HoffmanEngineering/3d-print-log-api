using System.Linq.Expressions;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;

namespace PrintLogApi.Profiles;

public class PrintProfile : Profile
{
    /// <summary>A print's actual material: each row's actual side, plus the actual "other filament" scalar.</summary>
    private static readonly Expression<Func<Print, int?>> ActualMaterialMg =
        ExpressionSplice.Inline<Print, Func<PrintFilament, double>, int?>(
            (src, row) => (int)Math.Round(src.FilamentUsage!.Sum(row))
                + (src.FilamentUsageMg.HasValue && src.FilamentUsageMg > 0 ? src.FilamentUsageMg.Value : 0),
            FilamentProfile.UsageRowActualMg);

    /// <summary>A print's estimated material: each row's estimated side, plus the estimated "other filament" scalar.</summary>
    private static readonly Expression<Func<Print, int?>> EstimatedMaterialMg =
        ExpressionSplice.Inline<Print, Func<PrintFilament, double>, int?>(
            (src, row) => (int)Math.Round(src.FilamentUsage!.Sum(row))
                + (src.EstimatedFilamentUsageMg.HasValue && src.EstimatedFilamentUsageMg > 0 ? src.EstimatedFilamentUsageMg.Value : 0),
            FilamentProfile.UsageRowEstimatedMg);

    private static readonly Expression<Func<Print, double?>> ActualMaterialG =
        ExpressionSplice.With<Print, int?, double?>((src, mg) => mg / 1000.0, ActualMaterialMg);

    private static readonly Expression<Func<Print, double?>> EstimatedMaterialG =
        ExpressionSplice.With<Print, int?, double?>((src, mg) => mg / 1000.0, EstimatedMaterialMg);

    public PrintProfile()
    {
        CreateMap<AddPrintDTO, Print>()
            .ForMember(dest => dest.ProjectId, opt => opt.Ignore())
            // Validated by AddPrintDTO, so a malformed one never gets here. AllowNull, or AutoMapper
            // maps a missing hash to an empty array, as it does any null collection.
            .ForMember(dest => dest.FileHash, opt =>
            {
                opt.AllowNull();
                opt.MapFrom(src => PrintFileHashes.Parse(src.FileHash));
            });

        CreateMap<Print, PrintSummaryDTO>()
            .ForMember(dest => dest.DefaultPrintImageId, opt => opt.MapFrom(src => src.Images!
                                                                    .Where(i => i.IsDefault == true)
                                                                    .Select(i => i.Id)
                                                                    .FirstOrDefault()))
            .ForMember(dest => dest.CreatedByUserId, opt => opt.MapFrom(src => src.CreatedById))
            .ForMember(dest => dest.CommentCount, opt => opt.MapFrom(src => src.Comments!.Select(c => c.Comment).Count()))
            // Mapped in memory after the query (PrintService loads each row's Filament), so the
            // shared rule runs as compiled code: each row resolved in whichever measure it
            // carries. The actual and estimated sums each read only their own side.
            .ForMember(dest => dest.SumActualFilamentWeightMg, opt => opt.MapFrom(src => (int)PrintMetrics.ActualRowsMg(src.FilamentUsage)))
            .ForMember(dest => dest.SumEstimatedFilamentWeightMg, opt => opt.MapFrom(src => (int)PrintMetrics.EstimatedRowsMg(src.FilamentUsage)))
            .ForMember(dest => dest.TotalFilamentWeightMg, opt => opt.MapFrom(src => (int)PrintMetrics.RowsMg(src.FilamentUsage)))
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
            .ForMember(dest => dest.CreatedDate, opt => opt.MapFrom(src => (DateTimeOffset)DateTime.SpecifyKind(src.CreatedDate, DateTimeKind.Utc)))
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
        // estimated columns stay SEPARATE — consumers resolve between them — so each reads only
        // its own side of FilamentProfile.UsageRowMg. Each term is guarded: zero or less means
        // "not recorded", so a corrupt negative contributes 0 instead of subtracting.
        // ProjectTo needs expressions, spliced from the shared rule; PrintProfileMaterialTests
        // pins the result.
        CreateMap<Print, PrintStatistic>()
            .ForMember(dest => dest.EstimatedFilamentUsageMg, opt => opt.MapFrom(EstimatedMaterialMg))
            .ForMember(dest => dest.FilamentUsageMg, opt => opt.MapFrom(ActualMaterialMg));

        CreateMap<Print, PrintDetailReport>()
            .ForMember(dest => dest.PrinterName, opt => opt.MapFrom(src => src.Printer.Name))
            .ForMember(dest => dest.PrinterMake, opt => opt.MapFrom(src => src.Printer.Make))
            .ForMember(dest => dest.PrinterModel, opt => opt.MapFrom(src => src.Printer.Model))
            // Same figures as PrintStatistic above, so the CSV export and the stats screen
            // cannot disagree.
            .ForMember(dest => dest.EstimatedFilamentUsageG, opt => opt.MapFrom(EstimatedMaterialG))
            .ForMember(dest => dest.FilamentUsageG, opt => opt.MapFrom(ActualMaterialG))
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
