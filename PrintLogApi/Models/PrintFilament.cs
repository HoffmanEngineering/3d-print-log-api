using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    public class PrintFilament
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public long PrintId { get; set; }

        public Print Print { get; set; }

        public Guid FilamentId { get; set; }
        public Filament Filament { get; set; }

        public int? EstimatedAmountMg { get; set; }
        public int? AmountMg { get; set; }
    }
}
