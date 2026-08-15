#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using AutoMapper;
using PrintLogApi.Enums;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;

namespace PrintLogApi.Profiles
{
    public class FilamentProfile : Profile
    {
        public FilamentProfile()
        {
            // Lightweight mapping for printer summary views - avoids expensive calculations
            CreateMap<Filament, FilamentSummaryForPrinterDto>()
                .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern ?? ColorPatternType.Solid))
                .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType ?? FilamentFinishType.Standard))
                .ForMember(dest => dest.Colors, src => src.MapFrom(src =>
                    src.Colors != null && src.Colors.Count > 0 ? src.Colors : new List<string> { src.ColorHex! }))
                .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects ?? new List<FilamentEffect>()));

            CreateMap<Filament, FilamentSummaryDto>()
                .ForMember(dest => dest.LoadedInPrinter, src => src.MapFrom(src => src.PrinterFilaments!.Where(pf => !pf.UnloadedDateTime.HasValue).Select(p => p.Printer).FirstOrDefault()))
                .ForMember(dest => dest.FilamentRemaining, src => src.MapFrom(src => (src.InitialNominalWeightMg ?? 0)
                                                                                    - src.PrintFilaments!.Sum(p => p.AmountMg.HasValue && p.AmountMg > 0 ?
                                                                                                                    (long) p.AmountMg :
                                                                                                                    p.EstimatedAmountMg.HasValue && p.EstimatedAmountMg > 0 ?
                                                                                                                    (long) p.EstimatedAmountMg : (long)0)
                                                                                    + src.FilamentAdjustments!.Sum(adj => adj.AmountMg)))
                .ForMember(dest => dest.FilamentLengthRemainingInM, src => src.MapFrom(src => src.DiameterMm > 0 && src.MaterialDensityGramPerCubicCm > 0 ? (((src.InitialNominalWeightMg ?? 0)
                                                                                    - src.PrintFilaments!.Sum(p => p.AmountMg.HasValue && p.AmountMg > 0 ?
                                                                                                                    (long)p.AmountMg :
                                                                                                                    p.EstimatedAmountMg.HasValue && p.EstimatedAmountMg > 0 ?
                                                                                                                    (long)p.EstimatedAmountMg : (long)0)
                                                                                    + src.FilamentAdjustments!.Sum(adj => adj.AmountMg)) ?? 0) / (250 * Math.PI * src.MaterialDensityGramPerCubicCm * src.DiameterMm * src.DiameterMm) : 0))
                .ForMember(dest => dest.FilamentVolumeRemainingInMl, src => src.MapFrom(src => ((src.InitialNominalVolumeMl ?? 0)
                                                                                    - src.PrintFilaments!.Sum(p => p.VolumeMl.HasValue && p.VolumeMl > 0 ?
                                                                                                                    p.VolumeMl :
                                                                                                                    p.EstimatedVolumeMl.HasValue && p.EstimatedVolumeMl > 0 ?
                                                                                                                    p.EstimatedVolumeMl : 0)
                                                                                    + src.FilamentAdjustments!.Sum(adj => adj.VolumeMl) ?? 0)))
                .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern ?? ColorPatternType.Solid))
                .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType ?? FilamentFinishType.Standard))
                .ForMember(dest => dest.Colors, src => src.MapFrom(src =>
                    src.Colors != null && src.Colors.Count > 0 ? src.Colors : new List<string> { src.ColorHex! }))
                .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects ?? new List<FilamentEffect>()))
                .ForMember(dest => dest.ColorHex, src => src.MapFrom(src =>
                    src.Colors != null && src.Colors.Count > 0 ? src.Colors[0] : src.ColorHex));

            CreateMap<FilamentSummaryDto, Filament>();

            CreateMap<AddFilamentDto, Filament>()
                .ForMember(dest => dest.MaterialCategoryNickname, src => src.MapFrom(src => !String.IsNullOrEmpty(src.MaterialCategoryNickname) ? src.MaterialCategoryNickname : "filament" ))
                .ForMember(dest => dest.Source, src => src.MapFrom(src => src.Source.HasValue ? src.Source : Filament.SourceMeasurement.Weight))
                .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern))
                .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType))
                .ForMember(dest => dest.Colors, src => src.MapFrom(src => src.Colors))
                .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects));

            CreateMap<EditFilamentDto, Filament>()
                .ForMember(dest => dest.MaterialCategoryNickname, src => src.MapFrom(src => !String.IsNullOrEmpty(src.MaterialCategoryNickname) ? src.MaterialCategoryNickname : "filament"))
                .ForMember(dest => dest.Source, src => src.MapFrom(src => src.Source.HasValue ? src.Source : Filament.SourceMeasurement.Weight))
                .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern))
                .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType))
                .ForMember(dest => dest.Colors, src => src.MapFrom(src => src.Colors))
                .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects));

            CreateMap<FilamentDetailDto, Filament>()
                .ForMember(dest => dest.Source, src => src.MapFrom(src => src.Source.HasValue ? src.Source : Filament.SourceMeasurement.Weight))
                .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern))
                .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType))
                .ForMember(dest => dest.Colors, src => src.MapFrom(src => src.Colors))
                .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects));

            CreateMap<Filament, FilamentDetailDto>()
                .ForMember(dest => dest.FilamentRemaining, src => src.MapFrom(src => src.InitialNominalWeightMg.HasValue
                                                                                    ? (long?)(src.InitialNominalWeightMg.Value
                                                                                    - src.PrintFilaments!.Sum(p => p.AmountMg.HasValue && p.AmountMg > 0 ?
                                                                                                                    (long)p.AmountMg :
                                                                                                                    p.EstimatedAmountMg.HasValue && p.EstimatedAmountMg > 0 ?
                                                                                                                    (long)p.EstimatedAmountMg : (long)0)
                                                                                    + src.FilamentAdjustments!.Sum(adj => adj.AmountMg))
                                                                                    : (long?)null))
                .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern ?? ColorPatternType.Solid))
                .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType ?? FilamentFinishType.Standard))
                .ForMember(dest => dest.Colors, src => src.MapFrom(src =>
                    src.Colors != null && src.Colors.Count > 0 ? src.Colors : new List<string> { src.ColorHex! }))
                .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects ?? new List<FilamentEffect>()))
                .ForMember(dest => dest.ColorHex, src => src.MapFrom(src =>
                    src.Colors != null && src.Colors.Count > 0 ? src.Colors[0] : src.ColorHex));

            CreateMap<FilamentAdjustment, FilamentAdjustmentDto>();

            CreateMap<FilamentAdjustmentDto, FilamentAdjustment>()
                .ForMember(dest => dest.Source, src => src.MapFrom(src => src.Source.HasValue ? src.Source : FilamentAdjustment.SourceMeasurement.Weight));
        }
    }
}
