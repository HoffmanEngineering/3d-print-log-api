using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print
{
    public class AddPrintDTO
    {
        public long PrinterId { get; set; }

        public int? EstimatedPrintTimeInSeconds { get; set; }
        public int? EstimatedFilamentUsageMg { get; set; }
        public int? PrintTimeInSeconds { get; set; }
        /// <summary>
        /// Filament usage in milligrams 
        /// </summary>
        public int? FilamentUsageMg { get; set; }

        public string FilamentType { get; set; }

        public string Notes { get; set; }

        public string Url { get; set; }

        public PrintStatus Status { get; set; }
    }
}
