using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Comments;

namespace PrintLogApi.Services;

public class CommentService(PrintLogContext context, IMapper mapper) : ICommentService
{
    public async Task<CommentDetailDto?> GetCommentDetailById(long id)
    {
        return await context.Comments
                        .Where(c => c.Id == id)
                        .AsNoTracking()
                        .ProjectTo<CommentDetailDto>(mapper.ConfigurationProvider)
                        .SingleOrDefaultAsync();
    }

    /// <summary>
    /// Delete a comment by id.
    /// </summary>
    /// <param name="id">The ID of the comment to delete.</param>
    /// <returns></returns>
    public async Task DeleteCommentById(long id)
    {
        var comment = await context.Comments
                        .Where(c => c.Id == id)
                        .SingleOrDefaultAsync();
        if (comment is null)
        {
            return;
        }

        // Find related links:
        var printComments = await context.PrintComments
                                .Where(pc => pc.CommentId == id)
                                .ToListAsync();

        if (printComments.Any())
        {
            context.PrintComments.RemoveRange(printComments);
        }

        // Remove Notifications referencing this comment.
        var notifications = await context.Notifications
            .Where(n => n.CommentId == id)
            .ToListAsync();
        context.Notifications.RemoveRange(notifications);

        context.Comments.Remove(comment);
        await context.SaveChangesAsync();

    }
}
