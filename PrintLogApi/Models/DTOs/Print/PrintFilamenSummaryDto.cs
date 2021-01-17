using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.Filament;

namespace PrintLogApi.Models.DTOs.Print
{
    public class PrintFilamenSummaryDto
    {
        public Guid Id { get; set; }

        public FilamentSummaryDto Filament { get; set; }

        public int? EstimatedAmountMg { get; set; }
        public int? AmountMg { get; set; }

        public string Notes { get; set; }
    }
}
