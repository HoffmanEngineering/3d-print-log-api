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
                    opt => opt.MapFrom(src => src.Status == SubscriptionStatus.Active))
                .ForMember(dest => dest.Status,
                    opt => opt.MapFrom(src => MapStatus(src.Status)))
                .ForMember(dest => dest.Plan,
                    opt => opt.MapFrom(src => MapPlan(src.Plan)));
        }

        private static string MapStatus(SubscriptionStatus status) => status switch
        {
            SubscriptionStatus.Active => "active",
            SubscriptionStatus.PastDue => "past_due",
            SubscriptionStatus.Canceled => "canceled",
            _ => "none"
        };

        private static string MapPlan(SubscriptionPlan plan) => plan switch
        {
            SubscriptionPlan.ProMonthly => "pro_monthly",
            SubscriptionPlan.ProAnnual => "pro_annual",
            _ => "free"
        };
    }
}
