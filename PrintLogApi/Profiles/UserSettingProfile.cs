using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.UserSetting;

namespace PrintLogApi.Profiles
{
    public class UserSettingProfile : Profile
    {
        public UserSettingProfile()
        {
            CreateMap<UserSetting, UserSettingDto>();

            CreateMap<AddUserSettingDto, UserSetting>();
            CreateMap<UpdateUserSettingDto, UserSetting>();
        }
    }
}
