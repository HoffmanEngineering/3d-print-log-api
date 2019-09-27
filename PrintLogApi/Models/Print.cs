using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    public class Print : TimestampEntity
    {
        public enum PrintStatus
        {
            Pending,
            Printing,
            Success,
            Cancelled,
            Failed
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        [MaxLength(100)]
        public string Title { get; set; }

        public DateTimeOffset? StartDate { get; set; }

        public long PrinterId { get; set; }
        public virtual Printer printer { get; set; }

        public int? EstimatedPrintTimeInSeconds { get; set; }
        public int? EstimatedFilamentUsageMg  { get; set; }
        public int? PrintTimeInSeconds { get; set; }
        /// <summary>
        /// Filament usage in milligrams 
        /// </summary>
        public int? FilamentUsageMg { get; set; }

        public string FilamentType { get; set; }

        [MaxLength(1000)]
        public string Notes { get; set; }

        [MaxLength(1000)]
        public string Url { get; set; }

        public PrintStatus Status { get; set; }
    }
}
