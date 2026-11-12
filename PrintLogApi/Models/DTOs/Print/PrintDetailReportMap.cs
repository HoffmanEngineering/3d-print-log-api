using CsvHelper.Configuration;

namespace PrintLogApi.Models.DTOs.Print;

public class PrintDetailReportMap : ClassMap<PrintDetailReport>
{
    public PrintDetailReportMap()
    {
        Map(p => p.StartDate).Index(0).Name("Start Date");
        Map(p => p.Title).Index(1).Name("Title").Convert(r => EscapeFormula(r.Value.Title));
        Map(p => p.PrinterName).Index(2).Name("Printer Name").Convert(r => EscapeFormula(r.Value.PrinterName));
        Map(p => p.PrinterMake).Index(3).Name("Printer Make").Convert(r => EscapeFormula(r.Value.PrinterMake));
        Map(p => p.PrinterModel).Index(4).Name("Printer Model").Convert(r => EscapeFormula(r.Value.PrinterModel));
        Map(p => p.EstimatedPrintTimeInSeconds).Index(5).Name("Estimated Print Time (s)");
        Map(p => p.EstimatedFilamentUsageG).Index(6).Name("Estimated Filament Usage (g)");
        Map(p => p.PrintTimeInSeconds).Index(7).Name("Print Time (s)");
        Map(p => p.FilamentUsageG).Index(8).Name("Filament Usage (g)");
        Map(p => p.FilamentType).Index(9).Name("Filament Type").Convert(r => EscapeFormula(r.Value.FilamentType));
        Map(p => p.Notes).Index(10).Name("Notes").Convert(r => EscapeFormula(r.Value.Notes));
        Map(p => p.Url).Index(11).Name("Url").Convert(r => EscapeFormula(r.Value.Url));
        Map(p => p.Status).Index(12).Name("Status");
        Map(p => p.ViewStatus).Index(13).Name("View Status");
        // Appended, never inserted (#294): spreadsheets built on this export address columns by position.
        Map(p => p.EstimatedFilamentUsageMl).Index(14).Name("Estimated Filament Usage (mL)");
        Map(p => p.FilamentUsageMl).Index(15).Name("Filament Usage (mL)");
    }

    /// <summary>
    /// Spreadsheets run a cell that starts with one of these as a formula.
    /// </summary>
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>
    /// Prefixes an apostrophe to free text that a spreadsheet would run as a formula. Titles, notes,
    /// URLs and printer and filament names come from users, connectors and file names. Only those
    /// text columns are escaped: CsvHelper's global <c>InjectionOptions.Escape</c> would also turn a
    /// negative number into text, and <c>Strip</c> would lose the leading character.
    /// </summary>
    internal static string? EscapeFormula(string? value) =>
        string.IsNullOrEmpty(value) || Array.IndexOf(FormulaStarts, value[0]) < 0 ? value : "'" + value;
}
