using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.Printer;

namespace PrintLogApi.Models.DTOs.Filament
{
    public class FilamentSummaryDto
    {
        public Guid Id { get; set; }

        /// <summary>
        /// Common name for the roll of filament.
        /// </summary>
        [StringLength(255)]
        public string DisplayName { get; set; }

        [StringLength(255)]
        public string Brand { get; set; }


        /// <summary>
        /// The material Type, ie PLA, PETG, ABS.
        /// </summary>
        [StringLength(255)]
        public string MaterialType { get; set; }

        /// <summary>
        /// The Density of the Material
        /// </summary>
        public double MaterialDensityGramPerCubicCm { get; set; }

        [StringLength(255)]
        public string ColorName { get; set; }
        [StringLength(6)]
        public string ColorHex { get; set; }

        /// <summary>
        /// The user's recommended temperature
        /// </summary>
        public double? RecommendedTemp { get; set; }
        public bool IsActive { get; set; }

        [StringLength(1000)]
        public string Notes { get; set; }

        public DateTime CreatedDate { get; set; }

        public long? FilamentRemaining { get; set; }

        public double? FilamentLengthRemainingInM { get; set; }

        public double? FilamentVolumeRemainingInMl { get; set; }
        /// <summary>
        /// Any notes about the purchase price
        /// </summary>
        [StringLength(1000)]
        public string PurchasePriceValue { get; set; }

        public long? InitialNominalWeightMg { get; set; }

        public double? DiameterMm { get; set; }

        public PrinterSummary LoadedInPrinter { get; set; }
        public string StorageLocation { get; set; }

        public bool IsFavorite { get; set; }
    }
}
