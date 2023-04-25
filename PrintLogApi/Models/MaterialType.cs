using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    /// <summary>
    /// Used as a material library of standard materials types.
    /// </summary>
    public class MaterialType
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        [StringLength(255)]
        public string Acronym { get; set; }

        [StringLength(255)]
        public string Name { get; set; }

        /// <summary>
        /// The Density of the Material
        /// </summary>
        public double DensityGramPerCubicCm { get; set; }

        public string MaterialCategoryNickname {  get; set; }
        public MaterialCategory MaterialCategory { get; set; }
    }
}
