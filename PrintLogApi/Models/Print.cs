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
            Pending = 1,
            Printing = 2,
            Success = 3,
            Cancelled = 4,
            Failed = 5
        }

        public enum PrintViewStatus
        {
            Public = 1,
            Unlisted = 2,
            Private = 3,
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        [MaxLength(100)]
        public string Title { get; set; }

        public DateTimeOffset? StartDate { get; set; }

        public long PrinterId { get; set; }
        public virtual Printer Printer { get; set; }

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

        public virtual ICollection<PrintImage> Images { get; set; }

        public PrintStatus Status { get; set; }

        public PrintViewStatus ViewStatus { get; set; }
    }
}
