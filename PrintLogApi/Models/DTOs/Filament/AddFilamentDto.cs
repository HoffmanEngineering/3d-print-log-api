using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.Filament
{
    public class AddFilamentDto
    {
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
        public double? DiameterMm { get; set; }
        public long? InitialTotalWeightMg { get; set; }

        /// <summary>
        /// The initial nominal weight of the filament in milligrams.
        /// </summary>
        /// <example>1kg roll would have an InitialNomialWeightMg of 1,000,000</example>
        public long? InitialNominalWeightMg { get; set; }
        /// <summary>
        /// The weight of the spool in milligrams.
        /// </summary>
        public long? SpoolWeightMg { get; set; }
        public double? TempRangeStart { get; set; }
        public double? TempRangeEnd { get; set; }
        /// <summary>
        /// The user's recommended temperature
        /// </summary>
        public double? RecommendedTemp { get; set; }
        public bool IsActive { get; set; }
        public DateTimeOffset? PurchaseDate { get; set; }

        /// <summary>
        /// The location (either URL, or physical location where the filament was purchased)
        /// </summary>
        [StringLength(1000)]
        public string PurchaseLocation { get; set; }
        [StringLength(256)]
        public string PurchasePriceValue { get; set; }
        /// <summary>
        /// The Currency Marker (ie, USD)
        /// </summary>
        [StringLength(256)]
        public string PurchasePriceCurrency { get; set; }

        [StringLength(1000)]
        public string Notes { get; set; }

        public ICollection<FilamentAdjustmentDto> FilamentAdjustments { get; set; }
    }
}
