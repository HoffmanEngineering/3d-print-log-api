using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models
{
    public class Printer
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        public long UserId { get; set; }
        public User User { get; set; }

        [MaxLength(50)]
        public string Make { get; set; }

        [MaxLength(50)]
        public string Model { get; set; }

        [MaxLength(100)]
        public string Name { get; set; }

        [MaxLength(1000)]
        public string Description { get; set; }

        public double NozzleDiameter { get; set; }

        public double FilamentDiameter { get; set; }

        public bool IsActive { get; set; }

        public ICollection<PrinterFilament> LoadedFilaments { get; set; }
    }
}
