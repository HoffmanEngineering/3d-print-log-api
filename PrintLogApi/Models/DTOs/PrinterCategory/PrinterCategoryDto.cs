using System.ComponentModel.DataAnnotations;
using PrintLogApi.Models.DTOs.MaterialCategory;

namespace PrintLogApi.Models.DTOs.PrinterCategory
{
    /// <summary>
    /// The class/category/type of 3D printing technology, such as FDM, SLA, etc
    /// </summary>
    public class PrinterCategoryDto
    {
        /// <summary>
        /// The nickname of the category
        /// </summary>
        [Key]
        [StringLength(50)]
        public string? Nickname { get; set; }

        /// <summary>
        /// The long form name of the category
        /// </summary>
        [StringLength(50)]
        public string? Name { get; set; }

        /// <summary>
        /// A description of that category
        /// </summary>
        [StringLength(255)]
        public string? Description { get; set; }

        /// <summary>
        /// The type of material that this 3D printing technology can use
        /// </summary>
        public MaterialCategoryDto? MaterialCategory { get; set; }

        public bool ShowNozzleDiameter { get; set; }
        public bool ShowFilamentDiameter { get; set; }
        public bool ShowBeamDiameter { get; set; }
        public bool ShowBedSize { get; set; }
        public bool ShowScreenResolution { get; set; }
        public bool ShowHasHeatedBed { get; set; }
        public bool ShowHasHeatedChamber { get; set; }
    }
}
