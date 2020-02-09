using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs
{
    public class UserPrinterDTO
    {
        public long PrinterId { get; set; }

        public string Name { get; set; }
        public string Make { get; set; }
        public string Model { get; set; }

    }
}
