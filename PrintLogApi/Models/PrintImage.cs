using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    public class PrintImage: TimestampEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public long PrintId { get; set; }

        public Print Print { get; set; }

        public Guid FileId { get; set; }
        public File File { get; set; }

        public bool IsDefault { get; set; }
    }
}
