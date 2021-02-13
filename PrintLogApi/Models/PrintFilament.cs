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

        public Guid? FilamentId { get; set; }

        // Can be null
        public Filament Filament { get; set; }

        public int? EstimatedAmountMg { get; set; }
        public int? AmountMg { get; set; }

        /// <summary>
        /// The estimated length of filament used in meters.
        /// </summary>
        public double? EstimatedLengthInM { get; set; }

        /// <summary>
        /// The actual length of filament used in meters.
        /// </summary>
        public double? LengthInM { get; set; }

        /// <summary>
        /// Determines if the user entered the length as the "source of truth", or weight. 
        /// True means the user entered the filament usage as a length, while false means the user entered usage as Mg.
        /// </summary>
        public bool IsEstimatedLengthSource { get; set; }

        /// <summary>
        /// Determines if the user entered the length as the "source of truth", or weight. 
        /// True means the user entered the filament usage as a length, while false means the user entered usage as Mg.
        /// </summary>
        public bool IsActualLengthSource { get; set; }

        [StringLength(1000)]
        public string Notes { get; set; }
    }
}
