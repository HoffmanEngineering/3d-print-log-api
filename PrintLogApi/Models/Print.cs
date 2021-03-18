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
            /// <summary>
            /// Publicly viewable by anyone.
            /// </summary>
            Public = 1,
            /// <summary>
            /// Anyone with the link can view, but print is not visible by searching.
            /// </summary>
            Unlisted = 2,
            /// <summary>
            /// Print can only be viewed by the creator.
            /// </summary>
            Private = 3,
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        [MaxLength(100)]
        public string Title { get; set; }

        public DateTimeOffset? StartDate { get; set; }

        public long PrinterId { get; set; }
        public Printer Printer { get; set; }

        public int? EstimatedPrintTimeInSeconds { get; set; }
        public int? EstimatedFilamentUsageMg  { get; set; }
        public int? PrintTimeInSeconds { get; set; }

        public ICollection<PrintFilament> FilamentUsage { get; set; }
        /// <summary>
        /// Filament usage in milligrams 
        /// </summary>
        public int? FilamentUsageMg { get; set; }

        [MaxLength(100)]
        public string FilamentType { get; set; }

        [MaxLength(1000)]
        public string Notes { get; set; }

        [MaxLength(1000)]
        public string Url { get; set; }

        public bool AllowComments { get; set; }

        /// <summary>
        /// The SHA1 file hash of the gcode file this print was created from.
        /// </summary>
        [MaxLength(20)]
        [MinLength(20)]
        public byte[] FileHash { get; set; }

        [MaxLength(1000)]
        public string FileName { get; set; }

        public ICollection<PrintImage> Images { get; set; }

        public ICollection<PrintComment> Comments { get; set; }


        public PrintStatus Status { get; set; }

        public PrintViewStatus ViewStatus { get; set; }
    }
}
