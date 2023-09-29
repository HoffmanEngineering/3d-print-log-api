using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.PrinterCategory;

namespace PrintLogApi.Models.DTOs.Printer
{
    public class PrinterDetailDto
    {
        public long? Id { get; set; }

        [MaxLength(50)]
        public string Make { get; set; }

        [MaxLength(50)]
        public string Model { get; set; }

        [MaxLength(100)]
        public string Name { get; set; }

        [MaxLength(1000)]
        public string Description { get; set; }

        public double NozzleDiameter { get; set; }

        public double FilamentDiameter { get; set; }

        public bool IsActive { get; set; }

        /// <summary>
        /// The collection of currently loaded filament for this printer.
        /// </summary>
        public ICollection<PrinterFilamentSummaryDto> LoadedFilaments { get; set; }

        public PrinterCategoryDto Type { get; set; }
    }
}
