using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
                .ForMember(dest => dest.FilamentRemaining, src => src.MapFrom(src => (src.InitialNominalWeightMg ?? 0)
                                                                                    - src.PrintFilaments.Sum(p => p.AmountMg.HasValue && p.AmountMg > 0 ? 
                                                                                                                    p.AmountMg : 
                                                                                                                    p.EstimatedAmountMg.HasValue && p.EstimatedAmountMg > 0 ? 
                                                                                                                    p.EstimatedAmountMg : 0) 
                                                                                    + src.FilamentAdjustments.Sum(adj => adj.AmountMg)));

            CreateMap<FilamentSummaryDto, Filament>();

            CreateMap<AddFilamentDto, Filament>();

            CreateMap<EditFilamentDto, Filament>();

            CreateMap<FilamentDetailDto, Filament>();
            CreateMap<Filament, FilamentDetailDto>();

            CreateMap<FilamentAdjustment, FilamentAdjustmentDto>().ReverseMap();
        }
    }
}
