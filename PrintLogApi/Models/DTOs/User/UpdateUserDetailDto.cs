using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using static PrintLogApi.Models.User;

namespace PrintLogApi.Models.DTOs.User
{
    public class UpdateUserDetailDto
    {
        /// <summary>
        /// URL pointing to the user's profile picture.
        /// </summary>
        public string? ProfilePicture { get; set; }

        /// <summary>
        /// URL pointing to the user's cover picture.
        /// </summary>
        public string? CoverPicture { get; set; }

        [StringLength(30, MinimumLength = 1)]
        public string? DisplayName { get; set; }

        [StringLength(1000)]
        public string? Bio { get; set; }

        public ProfileViewStatus ViewStatus { get; set; }
    }
}
