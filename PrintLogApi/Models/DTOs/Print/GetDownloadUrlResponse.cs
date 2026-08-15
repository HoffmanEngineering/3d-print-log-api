#nullable enable

using System;

namespace PrintLogApi.Models.DTOs.Print
{
    public class GetDownloadUrlResponse
    {
        public string? Url { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
