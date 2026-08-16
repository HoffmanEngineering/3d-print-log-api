using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.UserSetting
{
    public class AddUserSettingDto
    {

        public int UserSettingTypeId { get; set; }

        [StringLength(250)]
        public string? Value { get; set; }
    }
}
