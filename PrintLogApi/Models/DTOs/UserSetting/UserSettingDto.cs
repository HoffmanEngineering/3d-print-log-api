using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.UserSetting
{
    public class UserSettingDto
    {
        public long Id { get; set; }

        public long? UserId { get; set; }

        public int UserSettingTypeId { get; set; }

        public string Value { get; set; }

        public DateTime CreatedDate { get; set; }

        public DateTime UpdatedDate { get; set; }

    }
}
