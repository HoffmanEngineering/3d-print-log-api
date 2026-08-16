using System;
using System.ComponentModel.DataAnnotations;
using PrintLogApi.Models.DTOs.Printer;

namespace PrintLogApi.Models.DTOs.PrinterMaintenance
{
    public class PrinterMaintenanceDto
    {
        public Guid Id { get; set; }

        public long PrinterId { get; set; }

        public PrinterSummary? Printer { get; set; }

        public bool Done { get; set; }

        public DateTimeOffset Date { get; set; }

        [MaxLength(256)]
        public string? Category { get; set; }

        [MaxLength(1000)]
        public string? Description { get; set; }

        /// <summary>
        /// Additional Notes about the maintenance entry
        /// </summary>
        [MaxLength(1000)]
        public string? Notes { get; set; }

        /// <summary>
        /// The value of the purchase price
        /// </summary>
        [MaxLength(256)]
        public string? PriceValue { get; set; }
    }
}
