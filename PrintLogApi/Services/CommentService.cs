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

        /// <summary>
        /// Delete a comment by id.
        /// </summary>
        /// <param name="id">The ID of the comment to delete.</param>
        /// <returns></returns>
        public async Task DeleteCommentById(long id)
        {
            var comment = await _context.Comments
                            .Where(c => c.Id == id)
                            .SingleOrDefaultAsync();
            if (comment is null)
            {
                return;
            }

            // Find related links:
            var printComments = await _context.PrintComments
                                    .Where(pc => pc.CommentId == id)
                                    .ToListAsync();

            if (printComments.Any())
            {
                _context.PrintComments.RemoveRange(printComments);
            }

            // Remove Notifications referencing this comment.
            var notifications = await _context.Notifications
                .Where(n => n.CommentId == id)
                .ToListAsync();
            _context.Notifications.RemoveRange(notifications);

            _context.Comments.Remove(comment);
            await _context.SaveChangesAsync();

        }
    }
}
