using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Subscription
{
    public class CreateCheckoutSessionDto
    {
        [Required]
        public string? PlanId { get; set; }
    }
}
