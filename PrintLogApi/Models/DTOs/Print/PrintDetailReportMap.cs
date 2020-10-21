using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CsvHelper.Configuration;

namespace PrintLogApi.Models.DTOs.Print
{
    public class PrintDetailReportMap : ClassMap<PrintDetailReport>
    {
        public PrintDetailReportMap()
        {
            Map(p => p.StartDate).Index(0).Name("Start Date");
            Map(p => p.Title).Index(1).Name("Title");
            Map(p => p.PrinterName).Index(2).Name("Printer Name");
            Map(p => p.PrinterMake).Index(3).Name("Printer Make");
            Map(p => p.PrinterModel).Index(4).Name("Printer Model");
            Map(p => p.EstimatedPrintTimeInSeconds).Index(5).Name("Estimated Print Time (s)");
            Map(p => p.EstimatedFilamentUsageMg).Index(6).Name("Estimated Filament Usage (mg)");
            Map(p => p.PrintTimeInSeconds).Index(7).Name("Print Time (s)");
            Map(p => p.FilamentUsageMg).Index(8).Name("Printer MakeFilament Usage (mg)");
            Map(p => p.FilamentType).Index(9).Name("Filament Type");
            Map(p => p.Notes).Index(10).Name("Notes");
            Map(p => p.Url).Index(11).Name("Url");
            Map(p => p.Status).Index(12).Name("Status");
            Map(p => p.ViewStatus).Index(13).Name("View Status");
        }
    }
}
