using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Models.DTOs.Printer;

namespace PrintLogApi.Profiles
{
    public class PrinterProfile : Profile
    {
        public PrinterProfile()
        {
            CreateMap<Printer, UserPrinterDTO>()
                .ForMember(dest => dest.PrinterId, opt => opt.MapFrom(src => src));

            CreateMap<Printer, PrinterSummary>();
            CreateMap<Printer, PrinterDetailDto>();

            CreateMap<AddPrinterDTO, Printer>();


        }
    }
}
