using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    /// <summary>
    /// A way to adjust the amount of filament left on a spool.
    /// </summary>
    public class FilamentAdjustment: TimestampEntity
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

        public Guid FilamentId { get; set; }
        public Filament Filament { get; set; }

        public SourceMeasurement Source {  get; set; }

        /// <summary>
        /// An amount to adjust the weight of a filament.
        /// <para>
        ///     Amounts are added to the weight of the filament: 
        ///     <list type="bullet">
        ///        <item>Positive Adjustment Amounts mean the addition of filament to the roll.</item>
        ///         <item>Negative Adjustment Amounts mean the removal of filament of the roll.</item>
        ///     </list>
        /// </para>
        /// </summary>
        /// <example>If a filament has an INITIAL WEIGHT of 1000, and adjusted by +100, then the current weight is 1000+100 = 1100Mg </example>
        /// <example>If a filament has an INITIAL WEIGHT of 1000, and adjusted by -100, then the current weight is 1000-100) = 900 Mg</example>
        public long? AmountMg { get; set; }


        /// <summary>
        /// The length of filament used in meters.
        /// <para>
        ///     Amounts are added to the weight of the filament: 
        ///     <list type="bullet">
        ///        <item>Positive Adjustment Amounts mean the addition of filament to the roll.</item>
        ///         <item>Negative Adjustment Amounts mean the removal of filament of the roll.</item>
        ///     </list>
        /// </para>
        /// </summary>
        /// <example>If a filament has an INITIAL WEIGHT of 1000, and adjusted by +100, then the current weight is 1000+100 = 1100Mg </example>
        /// <example>If a filament has an INITIAL WEIGHT of 1000, and adjusted by -100, then the current weight is 1000-100) = 900 Mg</example>
        public double? LengthInM { get; set; }


        /// <summary>
        /// The volume of filament used in milliliters
        /// <para>
        ///     Amounts are added to the Volume of the filament: 
        ///     <list type="bullet">
        ///        <item>Positive Adjustment Amounts mean the addition of filament to the roll.</item>
        ///         <item>Negative Adjustment Amounts mean the removal of filament of the roll.</item>
        ///     </list>
        /// </para>
        /// </summary>
        /// <example>If a filament has an INITIAL VOLUME of 1000, and adjusted by +100, then the current weight is 1000+100 = 1100Mg </example>
        /// <example>If a filament has an INITIAL VOLUME of 1000, and adjusted by -100, then the current weight is 1000-100) = 900 Mg</example>
        public double? VolumeMl { get; set; }

        [StringLength(1000)]
        public string Notes { get; set; }

    }
}
