using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.Common;

namespace PrintLogApi.Models.DTOs.UserApiKeys
{
    public class NewUserApiKeyDto : TimestampDto
    {
        /// <summary>
        /// Not the API Key, just the primary key
        /// </summary>
        public Guid Id { get; set; }

        [StringLength(1000)]
        public string? Description { get; set; }

        /// <summary>
        ///  The new api key that was generated. This is the only time the API can return the public key
        /// </summary>
        public string? PublicKey { get; set; }

    }
}
