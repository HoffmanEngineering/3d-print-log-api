using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using PrintLogApi.Enums;

namespace PrintLogApi.Models.DTOs.Filament
{
    /// <summary>
    /// Minimal filament summary for printer display lists.
    /// Excludes expensive calculated fields (remaining weight/volume/length)
    /// to improve query performance.
    /// </summary>
    public class FilamentSummaryForPrinterDto
    {
        public Guid Id { get; set; }

        /// <summary>
        /// Common name for the roll of filament.
        /// </summary>
        [StringLength(255)]
        public string? DisplayName { get; set; }

        [StringLength(255)]
        public string? Brand { get; set; }

        /// <summary>
        /// The material Type, ie PLA, PETG, ABS.
        /// </summary>
        [StringLength(255)]
        public string? MaterialType { get; set; }

        [StringLength(255)]
        public string? ColorName { get; set; }

        [StringLength(6)]
        public string? ColorHex { get; set; }

        public ColorPatternType ColorPattern { get; set; }

        public List<string>? Colors { get; set; }

        public FilamentFinishType FinishType { get; set; }

        public List<FilamentEffect>? Effects { get; set; }
    }
}
