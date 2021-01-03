using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    public class TimestampEntity
    {

        public DateTime CreatedDate { get; set; }


        public long CreatedById { get; set; }
        [ForeignKey("CreatedById")]
        public User CreatedBy { get; set; }

        public DateTime UpdatedDate { get; set; }


        public long UpdatedById { get; set; }
        [ForeignKey("UpdatedById")]
        public User UpdatedBy {get; set;}
    }
}
