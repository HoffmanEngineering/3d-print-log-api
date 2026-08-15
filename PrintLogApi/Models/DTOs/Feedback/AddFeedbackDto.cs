#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using static PrintLogApi.Models.Feedback;

namespace PrintLogApi.Models.DTOs.Feedback
{
    public class AddFeedbackDto
    {
        public FeedbackType Type { get; set; }

        [StringLength(1000)]
        public string? Email { get; set; }

        [StringLength(5000)]
        public string? Note { get; set; }
    }
}
