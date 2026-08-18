namespace PrintLogApi.Models.DTOs.Print;

/// <summary>
/// The per-id outcome of a bulk operation. A bulk request is a 200 even when some ids
/// could not be acted on; the body is what says which.
/// </summary>
public class BulkPrintResultDto
{
    public List<long> Succeeded { get; set; } = [];

    public List<BulkPrintFailureDto> Failed { get; set; } = [];
}

/// <summary>
/// One id that was not acted on. <see cref="Reason"/> is a plain string, not a serialized
/// enum, so it stays readable without depending on JSON converter configuration.
/// </summary>
public class BulkPrintFailureDto
{
    public long Id { get; set; }

    /// <summary>Either "NotFound" or "Forbidden".</summary>
    public string Reason { get; set; } = string.Empty;
}
