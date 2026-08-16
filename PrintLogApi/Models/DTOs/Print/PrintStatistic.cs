using System;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print
{
    /// <summary>
    /// Used to get large collection of prints to run statistics from.
    /// </summary>
    public class PrintStatistic
    {
        public long Id { get; set; }

        public long PrinterID { get; set; }

        /// <summary>
        /// Nullable, because Print.StartDate is. Declared non-nullable, an undated print
        /// serialized as 0001-01-01T00:00:00+00:00, which every consumer reads as a real date in
        /// year 1: it sorts first, falls outside every range filter, and renders as a garbage
        /// date. Handle the null case explicitly — never with .Value or ?? default.
        /// </summary>
        public DateTimeOffset? StartDate { get; set; }

        public PrintStatus Status { get; set; }

        public int? EstimatedPrintTimeInSeconds { get; set; }
        public int? EstimatedFilamentUsageMg { get; set; }
        public int? PrintTimeInSeconds { get; set; }
        /// <summary>
        /// Filament usage in milligrams 
        /// </summary>
        public int? FilamentUsageMg { get; set; }


    }
}
