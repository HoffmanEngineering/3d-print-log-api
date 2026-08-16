using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Materials;

namespace PrintLogApi.Profiles
{
    public class MaterialTypeProfile : Profile
    {
        public MaterialTypeProfile()
        {
            CreateMap<MaterialType, MaterialTypeDto>();
        }

    }
}
