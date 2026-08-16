using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.UserApiKeys;

namespace PrintLogApi.Profiles;

public class UserApiKeyProfile : Profile
{

    public UserApiKeyProfile()
    {
        CreateMap<UserApiKey, UserApiKeyDto>();
    }
}
