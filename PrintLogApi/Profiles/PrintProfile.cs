using System.Linq;
using AutoMapper;
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

            CreateMap<Print, PrintStatistic>();

            CreateMap<Print, PrintDetailReport>()
                .ForMember(dest => dest.PrinterName, opt => opt.MapFrom(src => src.Printer.Name))
                .ForMember(dest => dest.PrinterMake, opt => opt.MapFrom(src => src.Printer.Make))
                .ForMember(dest => dest.PrinterModel, opt => opt.MapFrom(src => src.Printer.Model));

            CreateMap<PrintFilament, PrintFilamentDetailsDto>()
                .ForMember(dest => dest.filament, opt => opt.MapFrom(src => src.Filament));

        }
    }

}
