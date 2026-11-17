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

        // The pictures are shown to every visitor, so PUT Users/me never copies them blindly: the
        // upload endpoints write them, and UsersController.UpdateCurrentUserDetails decides about
        // the one value the web app sends itself (the Auth0 picture at first sign-in).
        CreateMap<UpdateUserDetailDto, User>()
            .ForMember(u => u.ProfilePicture, o => o.Ignore())
            .ForMember(u => u.CoverPicture, o => o.Ignore());
    }

}
