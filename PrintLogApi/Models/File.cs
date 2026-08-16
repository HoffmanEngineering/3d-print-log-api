using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models;

public class File : TimestampEntity
{
    [Key]
    public Guid Id { get; set; }

    public string? Path { get; set; }

    public long Size { get; set; }
}
