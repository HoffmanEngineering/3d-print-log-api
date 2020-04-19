using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    public class Feedback : TimestampEntity
    {
        public enum FeedbackType
        {
            Question = 1,
            Bug = 2,
            Suggestion = 3,
            Other = 4
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public FeedbackType Type { get; set; }

        [MaxLength(1000)]
        public string Email { get; set; }

        [MaxLength(5000)]
        public string Note { get; set; }
    }
}
