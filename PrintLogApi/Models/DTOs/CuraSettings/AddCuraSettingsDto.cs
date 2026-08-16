using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.CuraSettings
{
    public class AddCuraSettingsDto
    {
        [StringLength(100)]
        public string? CuraVersion { get; set; }

        [StringLength(100)]
        public string? PluginVersion { get; set; }

        public dynamic? Settings { get; set; }
    }
}
