using System;

namespace PrintLogApi.Models.DTOs.Common
{
    public class TimestampDto
    {
        public DateTime CreatedDate { get; set; }

        public long CreatedById { get; set; }

        public DateTime UpdatedDate { get; set; }

        public long UpdatedById { get; set; }

    }
}
