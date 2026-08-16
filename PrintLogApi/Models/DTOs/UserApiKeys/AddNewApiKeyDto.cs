using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.UserApiKeys
{
    public class AddNewApiKeyDto
    {
        [StringLength(1000)]
        public string? Description { get; set; }
    }
}
