using PrintLogApi.Models.DTOs.User;

namespace PrintLogApi.Models.DTOs.Comments;

public class CommentDetailDto
{
    public long Id { get; set; }

    public string? Body { get; set; }

    public DateTime CreatedDate { get; set; }
    public long CreatedById { get; set; }
    public UserSummaryDto? CreatedBy { get; set; }

    public DateTime UpdatedDate { get; set; }

    public long UpdatedById { get; set; }

    public UserSummaryDto? UpdatedBy { get; set; }


}
