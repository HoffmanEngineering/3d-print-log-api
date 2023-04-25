using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models
{
    /// <summary>
    /// The category of materials
    /// </summary>
    public class MaterialCategory
    {
        /// <summary>
        /// The nickname of the material category
        /// </summary>
        [Key]
        [StringLength(50)]
        public string Nickname { get; set; }

        /// <summary>
        /// The long form name of the category
        /// </summary>
        [StringLength(255)]
        public string Name { get; set; }

        /// <summary>
        /// A description of that category
        /// </summary>
        [StringLength(255)]
        public string Description { get; set; }

    }
}
