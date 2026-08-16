namespace PrintLogApi.Models.DTOs.Printer
{
    public class PrinterFeedSummary
    {
        public long Id { get; set; }

        public string? Name { get; set; }

        public string? Make { get; set; }

        public string? Model { get; set; }

        public bool IsActive { get; set; }
    }
}
