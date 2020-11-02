using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Comments;

namespace PrintLogApi.Services
{
    public class CommentService : ICommentService
    {
        private PrintLogContext _context;
        private IMapper _mapper;
        private TelemetryClient _telemetry;

        public CommentService(PrintLogContext context, IMapper mapper, TelemetryClient telemetry)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
        }

        public async Task<CommentDetailDto> GetCommentDetailById(long id)
        {
            return await _context.Comments
                            .Where(c => c.Id == id)
                            .AsNoTracking()
                            .ProjectTo<CommentDetailDto>(_mapper.ConfigurationProvider)
                            .SingleOrDefaultAsync();
        }
    }
}
