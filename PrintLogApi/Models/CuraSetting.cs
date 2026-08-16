using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    public class CuraSetting
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        [StringLength(100)]
        public string? CuraVersion { get; set; }

        [StringLength(100)]
        public string? PluginVersion { get; set; }

        //// https://docs.microsoft.com/en-us/ef/core/modeling/backing-field
        internal string? _Settings { get; set; }

        [NotMapped]
        public JsonElement Settings
        {
            get
            {
                return JsonSerializer.Deserialize<JsonElement>(string.IsNullOrEmpty(_Settings) ? "{}" : _Settings);
            }
            set
            {
                _Settings = value.ToString();
            }
        }

        public DateTimeOffset CreatedDate { get; set; }

        /// <summary>
        /// The ID of the user that first loaded this setting, thus locking it to that user.
        /// </summary>
        public long? UserId { get; set; }

    }
}
