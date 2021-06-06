using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    /// <summary>
    /// Stores a link between a printer and loaded filament.
    /// </summary>
    public class PrinterFilament
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        public long PrinterId { get; set; }

        public Printer Printer { get; set; }

        public Guid FilamentId { get; set; }

        public Filament Filament { get; set; }

        /// <summary>
        /// When the filament was loaded.
        /// </summary>
        public DateTimeOffset LoadedDateTime { get; set; }

        /// <summary>
        /// When the filament was unloaded from the machine.
        /// </summary>
        public DateTimeOffset? UnloadedDateTime { get; set; }
    }
}
