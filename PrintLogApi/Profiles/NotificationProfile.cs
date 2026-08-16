using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Notification;

namespace PrintLogApi.Profiles;

public class NotificationProfile : Profile
{
    public NotificationProfile()
    {
        CreateMap<Notification, NotificationSummaryDto>()
            .ForMember(dest => dest.PrintTitle, opt => opt.MapFrom(src => src.Print != null ? src.Print.Title : null))
            .ForMember(dest => dest.TriggeredByUser, opt => opt.MapFrom(src => src.TriggeredByUser));

        CreateMap<Notification, NotificationDetailDto>()
            .ForMember(dest => dest.PrintTitle, opt => opt.MapFrom(src => src.Print != null ? src.Print.Title : null))
            .ForMember(dest => dest.TriggeredByUser, opt => opt.MapFrom(src => src.TriggeredByUser));
    }
}
