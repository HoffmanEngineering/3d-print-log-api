using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.PrinterMaintenance;

public class AddPrinterMaintenanceDto
{
    public long PrinterId { get; set; }

    public bool Done { get; set; }

    public DateTimeOffset Date { get; set; }

    [MaxLength(256)]
    public string? Category { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Additional Notes about the maintenance entry
    /// </summary>
    [MaxLength(1000)]
    public string? Notes { get; set; }

    /// <summary>
    /// The value of the purchase price
    /// </summary>
    [MaxLength(256)]
    public string? PriceValue { get; set; }
}
