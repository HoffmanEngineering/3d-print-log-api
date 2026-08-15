#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.Printer
{
    public class AddPrinterDTO
    {
        public long? Id { get; set; }

        [MaxLength(50)]
        [Required(AllowEmptyStrings = false)]
        public string? Make { get; set; }

        [MaxLength(50)]
        [Required(AllowEmptyStrings = false)]
        public string? Model { get; set; }

        [MaxLength(100)]
        [Required(AllowEmptyStrings = false)]
        public string? Name { get; set; }

        [MaxLength(1000)]
        public string? Description { get; set; }

        [Range(0, double.MaxValue, ErrorMessage = "Only positive number allowed")]
        public double? NozzleDiameter { get; set; }

        [Range(0, double.MaxValue, ErrorMessage = "Only positive number allowed")]
        public double? FilamentDiameter { get; set; }

        [Range(0, double.MaxValue, ErrorMessage = "Only positive number allowed")]
        public double? BeamDiameter { get; set; }

        /// <summary>
        /// The collection of currently loaded filament for this printer.
        /// </summary>
        public ICollection<AddPrinterFilamentDto>? LoadedFilaments { get; set; }

        public bool IsActive { get; set; }


        /// <summary>
        /// Nickname of the Printer Category for this printer (ie, FDM, FFF, etc)
        /// </summary>
        public string? Category { get; set; }

        public double? BedWidthMm { get; set; }
        public double? BedHeightMm { get; set; }
        public double? BedDepthMm { get; set; }
        public double? ScreenResolutionXPixels { get; set; }
        public double? ScreenResolutionYPixels { get; set; }

        public bool? HasHeatedBed { get; set; }
        public bool? HasHeatedChamber { get; set; }

        [Range(0, double.MaxValue, ErrorMessage = "Only positive number allowed")]
        public double? WattageW { get; set; }
    }
}
