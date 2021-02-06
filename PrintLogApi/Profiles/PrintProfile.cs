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
            CreateMap<AddPrintDTO, Print>();

            CreateMap<Print, PrintSummaryDTO>()
                .ForMember(dest => dest.DefaultPrintImageId, opt => opt.MapFrom(src => src.Images
                                                                        .Where(i => i.IsDefault == true)
                                                                        .Select(i => i.Id)
                                                                        .FirstOrDefault()))
                .ForMember(dest => dest.CreatedByUserId, opt => opt.MapFrom(src => src.CreatedById))
                .ForMember(dest => dest.CommentCount, opt => opt.MapFrom(src => src.Comments.Select(c => c.Comment).Count()));

            CreateMap<Print, PrintDetailDTO>()
                .ForMember(dest => dest.PrinterId, opt => opt.MapFrom(src => src.Printer.Id))
                .ForMember(dest => dest.Images, opt => opt.MapFrom(src => src.Images))
                .ForMember(dest => dest.CreatedByUserId, opt => opt.MapFrom(src => src.CreatedById))
                .ForMember(dest => dest.Comments, opt => opt.MapFrom(src => src.Comments.Select(c => c.Comment)));

            CreateMap<PrintDetailDTO, Print>()
                .ForMember(dest => dest.Printer, opt => opt.Ignore())
                .ForMember(dest => dest.Images, opt => opt.Ignore())
                .ForMember(dest => dest.Comments, opt => opt.Ignore());

            CreateMap<Print, PrintStatistic>()
                .ForMember(dest => dest.EstimatedFilamentUsageMg, opt => opt.MapFrom(src => src.FilamentUsage.Sum(p => p.EstimatedAmountMg.HasValue &&
                                                                                                                    p.EstimatedAmountMg > 0 ?
                                                                                                                    p.EstimatedAmountMg : 0)
                                                                                            + (src.EstimatedFilamentUsageMg ?? 0)))
                .ForMember(dest => dest.FilamentUsageMg, opt => opt.MapFrom(src => src.FilamentUsage.Sum(p => p.AmountMg.HasValue &&
                                                                                                                    p.AmountMg > 0 ?
                                                                                                                    p.AmountMg : 0)
                                                                                            + (src.FilamentUsageMg ?? 0)));

            CreateMap<Print, PrintDetailReport>()
                .ForMember(dest => dest.PrinterName, opt => opt.MapFrom(src => src.Printer.Name))
                .ForMember(dest => dest.PrinterMake, opt => opt.MapFrom(src => src.Printer.Make))
                .ForMember(dest => dest.PrinterModel, opt => opt.MapFrom(src => src.Printer.Model))
                .ForMember(dest => dest.EstimatedFilamentUsageG, opt => opt.MapFrom(src => (src.FilamentUsage.Sum(p => p.EstimatedAmountMg.HasValue && 
                                                                                                                    p.EstimatedAmountMg > 0 ?
                                                                                                                    p.EstimatedAmountMg : 0) 
                                                                                            + (src.EstimatedFilamentUsageMg ?? 0))/1000.0))
                .ForMember(dest => dest.FilamentUsageG, opt => opt.MapFrom(src => (src.FilamentUsage.Sum(p => p.AmountMg.HasValue &&
                                                                                                                    p.AmountMg > 0 ?
                                                                                                                    p.AmountMg : 0)
                                                                                            + (src.FilamentUsageMg ?? 0))/1000.0))
                // Combine the FilamentTypes with the display names of the filaments, deliminated by ;.
                .ForMember(dest => dest.FilamentType, opt => opt.MapFrom(src => (src.FilamentType + "; " + string.Join("; ", src.FilamentUsage.Select(f => f.Filament.DisplayName).ToList())).Trim().Trim(';').Trim()));

            CreateMap<PrintFilament, PrintFilamentSummaryDto>()
                .ForMember(dest => dest.Filament, opt => opt.MapFrom(src => src.Filament));

            CreateMap<PrintFilamentSummaryDto, PrintFilament>()
                .ForMember(dest => dest.Filament, opt => opt.Ignore());

        }
    }

}
