using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Subscription;

namespace PrintLogApi.Profiles
{
    public class SubscriptionProfile : Profile
    {
        public SubscriptionProfile()
        {
            CreateMap<Subscription, SubscriptionDto>()
                .ForMember(dest => dest.IsPro,
                    opt => opt.MapFrom(src => src.Status == SubscriptionStatus.Active));
        }
    }
}
