using AutoMapper;
using PrintLogApi.Models.DTOs.Materials;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.MaterialCategory;

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
