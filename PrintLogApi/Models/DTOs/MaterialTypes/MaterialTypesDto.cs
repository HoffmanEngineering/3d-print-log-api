using System;

namespace PrintLogApi.Models.DTOs.Materials
{
    public class MaterialTypeDto
    {
        public Guid Id { get; set; }

        public string Acronym { get; set; }

        public string Name { get; set; }

        public string MaterialCategoryNickname { get; set; }

        /// <summary>
        /// The Density of the Material
        /// </summary>
        public double DensityGramPerCubicCm { get; set; }
    }
}
