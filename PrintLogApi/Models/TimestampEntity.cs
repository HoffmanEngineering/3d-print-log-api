using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

public class TimestampEntity
{

    public DateTime CreatedDate { get; set; }


    public long CreatedById { get; set; }
    [ForeignKey("CreatedById")]
    public User CreatedBy { get; set; } = null!;

    public DateTime UpdatedDate { get; set; }


    public long UpdatedById { get; set; }
    [ForeignKey("UpdatedById")]
    public User UpdatedBy { get; set; } = null!;
}
