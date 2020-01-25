using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.Printer
{
    public class AddPrinterDTO
    {
       
            public long? Id { get; set; }

            [MaxLength(50)]
            public string Make { get; set; }

            [MaxLength(50)]
            public string Model { get; set; }

            [MaxLength(1000)]
            public string Description { get; set; }

            public double NozzleDiameter { get; set; }

            public double FilamentDiameter { get; set; }

}
}
