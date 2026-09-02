using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Notification;

namespace PrintLogApi.Profiles;

public class NotificationProfile : Profile
{
    public NotificationProfile()
    {
        // SpecifyKind matches PrintProfile: CreatedDate is stored as UTC but read back from
        // SQL Server as Unspecified, so the kind is reasserted rather than converted.
        CreateMap<Notification, NotificationSummaryDto>()
            .ForMember(dest => dest.PrintTitle, opt => opt.MapFrom(src => src.Print != null ? src.Print.Title : null))
            .ForMember(dest => dest.CreatedDate, opt => opt.MapFrom(src => (DateTimeOffset)DateTime.SpecifyKind(src.CreatedDate, DateTimeKind.Utc)))
            .ForMember(dest => dest.TriggeredByUser, opt => opt.MapFrom(src => src.TriggeredByUser));

        CreateMap<Notification, NotificationDetailDto>()
            .ForMember(dest => dest.PrintTitle, opt => opt.MapFrom(src => src.Print != null ? src.Print.Title : null))
            .ForMember(dest => dest.CreatedDate, opt => opt.MapFrom(src => (DateTimeOffset)DateTime.SpecifyKind(src.CreatedDate, DateTimeKind.Utc)))
            .ForMember(dest => dest.ReadDate, opt => opt.MapFrom(src => src.ReadDate.HasValue
                ? (DateTimeOffset?)DateTime.SpecifyKind(src.ReadDate.Value, DateTimeKind.Utc)
                : null))
            .ForMember(dest => dest.TriggeredByUser, opt => opt.MapFrom(src => src.TriggeredByUser));
    }
}
