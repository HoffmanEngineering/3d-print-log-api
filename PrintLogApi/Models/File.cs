#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    public class File : TimestampEntity
    {
        [Key]
        public Guid Id { get; set; }

        public string? Path { get; set; }

        public long Size { get; set; }
    }
}
