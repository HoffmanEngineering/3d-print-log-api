using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Materials;

namespace PrintLogApi.Profiles
{
    public class MaterialProfile: Profile
    {
        public MaterialProfile()
        {
            CreateMap<Material, MaterialDto>();
        }

    }
}
