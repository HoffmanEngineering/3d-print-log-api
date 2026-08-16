using System;
using System.ComponentModel.DataAnnotations;

using static PrintLogApi.Models.User;

namespace PrintLogApi.Models.DTOs
{
    public class UserDetailDto
    {
        public long Id { get; set; }

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

        /// <summary>
        ///   If present, the datetime that the user started the deactivation process.
        /// </summary>
        public DateTimeOffset? DeactivationDateTime { get; set; }

        public ProfileViewStatus ViewStatus { get; set; }
    }
}
