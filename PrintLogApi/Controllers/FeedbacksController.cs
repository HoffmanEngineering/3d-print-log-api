using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Feedback;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Manage feedback.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class FeedbacksController : ControllerBase
    {
        private readonly IFeedbackService _feedbackService;

        public FeedbacksController(IFeedbackService feedbackService)
        {
            _feedbackService = feedbackService;
        }

        /// <summary>
        ///     Send a feedback.
        /// </summary>
        /// <param name="requestDto">The feedback request.</param>
        /// <response code="201">Returned when feedback as been successfully sent.</response>
        /// <response code="401">Returned when the user is not authorized. Only logged-in users can send feedback.</response>
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status201Created)]
        public async Task<ActionResult> Post([FromBody] AddFeedbackDto requestDto, CancellationToken ct)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            await _feedbackService.CreateFeedback(
                userId.Value, requestDto.Type, requestDto.Email, requestDto.Note, ct);

            return StatusCode(201);
        }
    }
}
