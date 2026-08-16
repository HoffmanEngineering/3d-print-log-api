using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.MaterialCategory;
using PrintLogApi.Models.DTOs.Materials;

namespace PrintLogApi.Profiles
{
    public class MaterialCategoryProfile : Profile
    {
        public MaterialCategoryProfile()
        {
            CreateMap<MaterialCategory, MaterialCategoryDto>()
                .ReverseMap();
        }
    }
}
