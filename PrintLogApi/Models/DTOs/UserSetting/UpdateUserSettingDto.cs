using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.UserSetting
{
    public class UpdateUserSettingDto
    {
        public long Id { get; set; }

        public string Value { get; set; }
    }
}
