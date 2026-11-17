using System.Text.Json.Serialization;

namespace PrintLogApi.Models.DTOs.Moonraker;

/// <summary>
/// A print event from Moonraker's notifier. Sent JSON-encoded inside
/// <see cref="PrintEventDto.Message"/>, and echoed back decoded in the response.
/// </summary>
public class PrintEventMessageDto
{
    /// <summary>
    /// <c>started</c>, <c>complete</c>, <c>cancelled</c> or <c>error</c>. Anything else
    /// (<c>paused</c>, <c>resumed</c>) is accepted and ignored.
    /// </summary>
    [JsonPropertyName("event_name")]
    public string? EventName { get; set; }

    /// <summary>The filament used so far, in millimeters.</summary>
    [JsonPropertyName("filament_used")]
    public double FilamentUsed { get; set; }

    /// <summary>The G-code file's path on the printer.</summary>
    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>Time spent actually printing, in seconds.</summary>
    [JsonPropertyName("print_duration")]
    public double PrintDuration { get; set; }

    /// <summary>The id of one of the caller's printers.</summary>
    [JsonPropertyName("printerId")]
    public long PrinterId { get; set; }

    /// <summary>Klipper's print state, such as <c>printing</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>Total job time including pauses, in seconds.</summary>
    [JsonPropertyName("total_duration")]
    public double TotalDuration { get; set; }
}
