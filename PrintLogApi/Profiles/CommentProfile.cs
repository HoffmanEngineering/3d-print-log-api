using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Comments;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Profiles
{
    public class CommentProfile : Profile
    {
        public CommentProfile()
        {
            CreateMap<Comment, CommentDetailDto>()
                .ForMember(dest => dest.Comments, opt => opt.MapFrom(src => src.Comments));

        }
    }
}
