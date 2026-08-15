#nullable enable

using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.MaterialCategory
{
    /// <summary>
    /// The category of materials, such as filament, resin, powder, etc.
    /// </summary>
    public class MaterialCategoryDto
    {
        /// <summary>
        /// The nickname of the material category
        /// </summary>
        [StringLength(50)]
        public string? Nickname { get; set; }

        /// <summary>
        /// The long form name of the category
        /// </summary>
        [StringLength(255)]
        public string? Name { get; set; }

        /// <summary>
        /// A description of that category
        /// </summary>
        [StringLength(255)]
        public string? Description { get; set; }

        /// <summary>
        /// Whether this material has a diameter
        /// </summary>
        public bool HasDiameter { get; set; }

        public bool ShowNozzleTemperature { get; set; }

        public bool ShowBedTemperature { get; set; }

        public bool ShowMeltingTemperature { get; set; }

        public bool ShowRecommendedInitialLayerTimeInSeconds { get; set; }

        public bool ShowRecommendedLayerTimeInSeconds { get; set; }

        /// <summary>
        /// The percentage of new powder when mixing with old powder
        /// </summary>
        public bool ShowMaterialRefreshRatio { get; set; }

        public bool ShowInertGas { get; set; }
    }
}
