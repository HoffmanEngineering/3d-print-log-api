using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Models.DTOs.Feedback;
using PrintLogApi.Models.DTOs.Printer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Profiles
{
    public class FeedbackProfile: Profile
    {
        public FeedbackProfile()
        {
            CreateMap<AddFeedbackDto, Feedback>();

        }
    }
}
