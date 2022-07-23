using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Feedback;
using PrintLogApi.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using PrintLogApi.Services;
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;
        private readonly IEmailSender _emailSender;
        private readonly string _feedbackEmail;

        public FeedbacksController(PrintLogContext context, IMapper mapper, TelemetryClient telemetry, IEmailSender emailSender, IConfiguration config )
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
            _emailSender = emailSender;
            _feedbackEmail = config["FeedbackEmailAddress"];
        }

        /// <summary>
        ///     Send a feedback.
        /// </summary>
        /// <param name="requestDto">The feedback request.</param>
        /// <response code="201">Returned when feedback as been successfully sent.</response>
        /// <response code="401">Returned when the user is not authorized. Only logged-in users can send feedback.</response>
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status201Created)]
        public async Task<ActionResult> Post([FromBody] AddFeedbackDto requestDto)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var newFeedback = _mapper.Map<Feedback>(requestDto);

            newFeedback.CreatedById = userId.Value;
            newFeedback.UpdatedById = userId.Value;

            _context.Feedback.Add(newFeedback);
            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("FeedbackAdded");



            // Send Email
            if (!string.IsNullOrWhiteSpace(_feedbackEmail))
            {
                var user = await _context.Users.Where(u => u.Id == newFeedback.CreatedById).FirstOrDefaultAsync();

                var subject = "New 3D Print Log Feedback";
                var body = $@"
By: {System.Security.SecurityElement.Escape(user.DisplayName)} (User ID: {System.Security.SecurityElement.Escape(user.Id.ToString())}) <br>
Email: {System.Security.SecurityElement.Escape(newFeedback.Email)} <br>
Type: {Enum.GetName(typeof(Feedback.FeedbackType), newFeedback.Type)} <br>
Feedback ID: {newFeedback.Id} <br>
<br>
Feedback: <br>
{System.Security.SecurityElement.Escape(newFeedback.Note)}
";
                await _emailSender.SendEmailAsync(_feedbackEmail, subject, body);
            }

            return StatusCode(201);
        }
    }
}
