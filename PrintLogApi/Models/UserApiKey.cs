using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    public class UserApiKey : TimestampEntity
    {
        // Not the API Key, just the primary key
        public Guid Id { get; set; }

        public User User { get; set; }
        [Required]
        public long UserId { get; set; }

        [Required]
        [StringLength(100)]
        public string HashedKey { get; set; }

        [Required]
        [StringLength(16)]
        public string HashAlgorithm { get; set; }

        [StringLength(1000)]
        public string Description { get; set; }

        public bool IsDeleted { get; set; }

        /// <summary>
        /// The date that the api key was last used.
        /// </summary>
        public DateTimeOffset? LastUsed { get; set; }
    }
}
