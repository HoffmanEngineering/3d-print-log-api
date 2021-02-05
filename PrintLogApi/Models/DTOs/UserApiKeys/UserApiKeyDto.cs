using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.UserApiKeys
{
    public class UserApiKeyDto
    {
        /// <summary>
        /// Not the API Key, just the primary key
        /// </summary>
        public Guid Id { get; set; }

        [StringLength(1000)]
        public string Description { get; set; }

        public bool IsDeleted { get; set; }
    }
}
