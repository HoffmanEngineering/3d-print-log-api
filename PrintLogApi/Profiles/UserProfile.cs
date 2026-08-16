using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Models.DTOs.User;

namespace PrintLogApi.Profiles;

public class UserProfile : Profile
{
    public UserProfile()
    {
        CreateMap<User, UserDetailDto>();
        CreateMap<User, UserSummaryDto>();

        CreateMap<UpdateUserDetailDto, User>();
    }

}
