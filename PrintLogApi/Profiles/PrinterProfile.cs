using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Models.DTOs.Printer;

namespace PrintLogApi.Profiles;

public class PrinterProfile : Profile
{
    public PrinterProfile()
    {
        CreateMap<Printer, UserPrinterDTO>()
            .ForMember(dest => dest.PrinterId, opt => opt.MapFrom(src => src));

        CreateMap<Printer, PrinterSummary>();
        CreateMap<Printer, PrinterFeedSummary>();
        CreateMap<Printer, PrinterSummaryWithFilamentDto>();
        CreateMap<Printer, PrinterSummaryWithoutCategory>();
        CreateMap<Printer, PrinterDetailDto>();

        // New lightweight mapping for improved query performance
        CreateMap<Printer, PrinterSummarySimpleDto>();

        CreateMap<AddPrinterDTO, Printer>()
            .ForMember(dest => dest.Category, opt => opt.Ignore());

        CreateMap<PrinterFilament, PrinterFilamentSummaryDto>()
            .ForMember(dest => dest.Filament, opt => opt.MapFrom(src => src.Filament))
            .ReverseMap();

        // New lightweight filament mapping for summary views
        CreateMap<PrinterFilament, PrinterFilamentForSummaryDto>()
            .ForMember(dest => dest.Filament, opt => opt.MapFrom(src => src.Filament));

        CreateMap<AddPrinterFilamentDto, PrinterFilament>();

    }
}
