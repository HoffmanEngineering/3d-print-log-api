#nullable enable

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models
{
    public class UserSettingType
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [StringLength(100)]
        public string? Name { get; set; }

        [StringLength(200)]
        public string? Description { get; set; }

    }
}
