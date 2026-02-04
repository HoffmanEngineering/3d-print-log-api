using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Print
{
    public class ReorderImagesDto
    {
        [Required]
        public List<ImageOrderDto> Images { get; set; }
    }

    public class ImageOrderDto
    {
        public int ImageId { get; set; }
        public int DisplayOrder { get; set; }
    }
}
