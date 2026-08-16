using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Comments;

public class AddCommentDto
{
    [StringLength(2000)]
    public string? Body { get; set; }
}
