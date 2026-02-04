using System.Collections.Generic;

namespace PrintLogApi.Models.DTOs.Print
{
    public class ReorderImagesDto
    {
        public List<ImageOrderDto> Images { get; set; }
    }

    public class ImageOrderDto
    {
        public int ImageId { get; set; }
        public int DisplayOrder { get; set; }
    }
}
