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
    public class PrintImageProfile : Profile
    {
        public PrintImageProfile()
        {
            CreateMap<PrintImage, PrintImageDto>()
                .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
                .ForMember(dest => dest.IsDefault, opt => opt.MapFrom(src=> src.IsDefault));
        }
    }
}
