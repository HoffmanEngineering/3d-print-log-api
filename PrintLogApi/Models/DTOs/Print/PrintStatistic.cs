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

        public DateTimeOffset StartDate { get; set; }

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
