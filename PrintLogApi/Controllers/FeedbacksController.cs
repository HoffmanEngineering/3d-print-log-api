using AutoMapper;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Feedback;
using System.Security.Claims;
using System.Threading.Tasks;

namespace PrintLogApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class FeedbacksController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;

        public FeedbacksController(PrintLogContext context, IMapper mapper, TelemetryClient telemetry)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
        }

        // POST: api/Feedbacks
        [HttpPost]
        public async Task<ActionResult> Post([FromBody] AddFeedbackDto requestDto)
        {
            Feedback newFeedback = _mapper.Map<Feedback>(requestDto);

            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            newFeedback.CreatedById = userId;
            newFeedback.UpdatedById = userId;

            _context.Feedback.Add(newFeedback);
            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("FeedbackAdded");

            return StatusCode(201);
        }
    }
}
