#nullable enable

using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Comments
{
    public class EditCommentDto
    {
        [StringLength(2000)]
        public string? Body { get; set; }
    }
}
