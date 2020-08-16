using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Models.DTOs.Print;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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

        }
    }

    public class CustomResolver : IValueResolver<Print, PrintSummaryDTO, int?>
    {
        public int? Resolve(Print source, PrintSummaryDTO destination, int? member, ResolutionContext context)
        {
            return source.Comments.Select(c => c.Comment).Count() + source.Comments.Count;
        }
    }
}
