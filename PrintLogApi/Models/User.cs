using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    public class User
    {
        /// <summary>
        /// The View Access for the user's profile.
        /// </summary>
        public enum ProfileViewStatus
        {
            /// <summary>
            /// Anyone can search for and view
            /// </summary>
            Public = 1,

            /// <summary>
            /// Anyone with the direct link to the user can view
            /// </summary>
            Unlisted = 2,

            /// <summary>
            /// Only those who are friends with the user can view.
            /// </summary>
            Friends = 3,

            /// <summary>
            /// No one but the user can view.
            /// </summary>
            Private = 4,
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }
        public string? OAuthUserId { get; set; }

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

        public ICollection<Printer>? printers { get; set; }
    }
}
