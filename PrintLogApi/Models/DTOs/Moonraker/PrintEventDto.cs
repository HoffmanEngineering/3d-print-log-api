namespace PrintLogApi.Models.DTOs.Moonraker;

/// <summary>
/// The JSON notification Moonraker's notifier posts (an Apprise <c>json://</c> URL). Other
/// fields Apprise sends, such as <c>title</c> and <c>type</c>, are ignored.
/// </summary>
public class PrintEventDto
{
    /// <summary>
    /// The print event, as a JSON-encoded string rather than a nested object: a
    /// <c>PrintLogApi.Models.DTOs.Moonraker.PrintEventMessageDto</c> serialized to text, for example
    /// <c>{"event_name": "started", "filament_used": 0.0, "filename": "cube.gcode", "message": "", "print_duration": 0.0, "printerId": 7, "state": "printing", "total_duration": 0.0}</c>.
    /// </summary>
    public string? Message { get; set; }
}
