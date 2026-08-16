using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models
{
    public class PrintFilament
    {
        /// <summary>
        /// Which field is the user-entered "source"
        /// </summary>
        public enum SourceMeasurement
        {
            Weight = 1,
            Length = 2,
            Volume = 3,
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public long PrintId { get; set; }

        public Print Print { get; set; } = null!;

        public Guid? FilamentId { get; set; }

        // Can be null
        public Filament? Filament { get; set; }

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
        /// The estimated length of filament used in meters.
        /// </summary>
        public double? EstimatedVolumeMl{ get; set; }

        /// <summary>
        /// The actual volume of filament used in milliliters
        /// </summary>
        public double? VolumeMl { get; set; }

        /// <summary>
        /// The source of truth for the estimated sections
        /// </summary>
        public SourceMeasurement EstimatedSource { get; set; }

        /// <summary>
        /// The source of truth for the actual sections
        /// </summary>
        public SourceMeasurement Source {  get; set; }

        [StringLength(1000)]
        public string? Notes { get; set; }
    }
}
