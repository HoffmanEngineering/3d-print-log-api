using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    public class Comment : TimestampEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        public string Body { get; set; }

        public virtual User User { get; set; }

        public long UserId { get; set; }

        public long? ParentId { get; set; }
        public virtual Comment Parent { get; set; }
        public virtual List<Comment> Comments { get; set; }

    }
}
