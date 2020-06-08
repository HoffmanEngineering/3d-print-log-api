using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models
{
    public class UserSetting : TimestampEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        public long? UserId { get; set; }
        public virtual User User { get; set; }

        public int UserSettingTypeId { get; set; }
        public virtual UserSettingType UserSettingType { get; set; }

        [StringLength(250)]
        public string Value { get; set; }
    }
}
