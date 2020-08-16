using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.Comments
{
    public class CommentDetailDto
    {
        public long Id { get; set; }

        public string Body { get; set; }

        public long UserId { get; set; }
        public DateTime CreatedDate { get; set; }
        public long CreatedById { get; set; }
        public DateTime UpdatedDate { get; set; }

        public long UpdatedById { get; set; }

        public virtual List<CommentDetailDto> Comments { get; set; }
    }
}
