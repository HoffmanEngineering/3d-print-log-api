using System;
using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Filament
{
    public class FilamentAdjustmentDto
    {
        public Guid Id { get; set; }

        [Required]
        public Guid FilamentId { get; set; }

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
        [Required]
        public long AmountMg { get; set; }

        [StringLength(1000)]
        public string Notes { get; set; }

        public DateTime CreatedDate { get; set; }

        public DateTime UpdatedDate { get; set; }
    }
}
