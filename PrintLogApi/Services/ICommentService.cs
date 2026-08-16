using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.Comments;

namespace PrintLogApi.Services
{
    public interface ICommentService
    {
        Task DeleteCommentById(long id);
        Task<CommentDetailDto?> GetCommentDetailById(long id);
    }
}
