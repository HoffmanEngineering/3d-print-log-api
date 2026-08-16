using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.Printer
{
    public class AddPrinterFilamentDto
    {
        /// <summary>
        /// The GUID of the PrintFilament collection. Use EMPTY_GUID for new entries.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// The optional GUID of the filament used. Set as null (or EMPTY_GUID) to not 
        /// link this usage to a filament, and instead treat this as a non-tracked filament.
        /// </summary>
        public Guid FilamentId { get; set; }
    }
}
