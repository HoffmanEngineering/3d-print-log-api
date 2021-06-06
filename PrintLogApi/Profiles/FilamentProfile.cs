using System;
using System.Linq;
using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;

namespace PrintLogApi.Profiles
{
    public class FilamentProfile : Profile
    {
        public FilamentProfile()
        {
            CreateMap<Filament, FilamentSummaryDto>()
                .ForMember(dest => dest.LoadedInPrinter, src => src.MapFrom(src => src.PrinterFilaments.Where(pf => !pf.UnloadedDateTime.HasValue).Select(p => p.Printer).FirstOrDefault() ))
                .ForMember(dest => dest.FilamentRemaining, src => src.MapFrom(src => (src.InitialNominalWeightMg ?? 0)
                                                                                    - src.PrintFilaments.Sum(p => p.AmountMg.HasValue && p.AmountMg > 0 ?
                                                                                                                    p.AmountMg :
                                                                                                                    p.EstimatedAmountMg.HasValue && p.EstimatedAmountMg > 0 ?
                                                                                                                    p.EstimatedAmountMg : 0)
                                                                                    + src.FilamentAdjustments.Sum(adj => adj.AmountMg)))
                .ForMember(dest => dest.FilamentLengthRemainingInM, src => src.MapFrom(src => src.DiameterMm > 0 && src.MaterialDensityGramPerCubicCm > 0 ? (((src.InitialNominalWeightMg ?? 0)
                                                                                    - src.PrintFilaments.Sum(p => p.AmountMg.HasValue && p.AmountMg > 0 ?
                                                                                                                    p.AmountMg :
                                                                                                                    p.EstimatedAmountMg.HasValue && p.EstimatedAmountMg > 0 ?
                                                                                                                    p.EstimatedAmountMg : 0)
                                                                                    + src.FilamentAdjustments.Sum(adj => adj.AmountMg)) ?? 0) / (250 * Math.PI * src.MaterialDensityGramPerCubicCm * src.DiameterMm * src.DiameterMm) : 0));

            CreateMap<FilamentSummaryDto, Filament>();

            CreateMap<AddFilamentDto, Filament>();

            CreateMap<EditFilamentDto, Filament>();

            CreateMap<FilamentDetailDto, Filament>();
            CreateMap<Filament, FilamentDetailDto>();

            CreateMap<FilamentAdjustment, FilamentAdjustmentDto>().ReverseMap();
        }
    }
}
