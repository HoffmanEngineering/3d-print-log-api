using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Profiles;

public class PrintImageProfile : Profile
{
    public PrintImageProfile()
    {
        CreateMap<PrintImage, PrintImageDto>()
            .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
            .ForMember(dest => dest.IsDefault, opt => opt.MapFrom(src => src.IsDefault))
            .ForMember(dest => dest.DisplayOrder, opt => opt.MapFrom(src => src.DisplayOrder));

        CreateMap<PrintImageDto, PrintImage>();
    }
}
