using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.Materials
{
    public class MaterialDto
    {
        public Guid Id { get; set; }

        public string Acronym { get; set; }

        public string Name { get; set; }

        /// <summary>
        /// The Density of the Material
        /// </summary>
        public double DensityGramPerCubicCm { get; set; }
    }
}
