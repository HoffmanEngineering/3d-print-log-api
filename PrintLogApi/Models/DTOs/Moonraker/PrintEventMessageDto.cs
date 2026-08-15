#nullable enable

using System.Text.Json.Serialization;

namespace PrintLogApi.Models.DTOs.Moonraker
{
    public class PrintEventMessageDto
    {
        /// '{"event_name": "started", "filament_used": 0.0, "filename": "udisk/CFFFP_xyzCalibration_cube.gcode", "message": "", "print_duration": 0.0, "printerId": 7, "state": "printing", "total_duration": 0.0009534579999126436}'
        /// 

        [JsonPropertyName("event_name")]
        public string? EventName { get; set; }
        [JsonPropertyName("filament_used")]
        public double FilamentUsed { get; set; }
        [JsonPropertyName("filename")]
        public string? Filename { get; set; }
        [JsonPropertyName("message")]
        public string? Message { get; set; }
        [JsonPropertyName("print_duration")]
        public double PrintDuration { get; set; }
        [JsonPropertyName("printerId")]
        public long PrinterId {  get; set; }
        [JsonPropertyName("state")]
        public string? State { get; set; }
        [JsonPropertyName("total_duration")]
        public double TotalDuration { get; set; }

    }
}
