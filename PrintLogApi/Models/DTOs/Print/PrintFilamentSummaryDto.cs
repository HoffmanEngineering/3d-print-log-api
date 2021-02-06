using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.Filament;

namespace PrintLogApi.Models.DTOs.Print
{
    public class PrintFilamentSummaryDto
    {
        public Guid Id { get; set; }

        public FilamentSummaryDto Filament { get; set; }

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
        public bool LengthIsSource { get; set; }

        public string Notes { get; set; }
    }
}
