using System.ComponentModel.DataAnnotations;
using static PrintLogApi.Models.Feedback;

namespace PrintLogApi.Models.DTOs.Feedback;

public class AddFeedbackDto
{
    public FeedbackType Type { get; set; }

    [StringLength(1000)]
    public string? Email { get; set; }

    [StringLength(5000)]
    public string? Note { get; set; }
}
