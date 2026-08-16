using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Print;

public class ReorderImagesDto
{
    [Required]
    public List<ImageOrderDto>? Images { get; set; }
}

public class ImageOrderDto
{
    [Range(1, int.MaxValue, ErrorMessage = "ImageId must be a valid image identifier.")]
    public int ImageId { get; set; }
    [Range(0, int.MaxValue, ErrorMessage = "DisplayOrder must be a non-negative integer.")]
    public int DisplayOrder { get; set; }
}
