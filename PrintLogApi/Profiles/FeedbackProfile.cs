using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Feedback;

namespace PrintLogApi.Profiles
{
    public class FeedbackProfile : Profile
    {
        public FeedbackProfile()
        {
            CreateMap<AddFeedbackDto, Feedback>();

        }
    }
}
