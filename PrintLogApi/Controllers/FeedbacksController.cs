using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Feedback;

namespace PrintLogApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class FeedbacksController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;

        public FeedbacksController(PrintLogContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        // GET: api/Feedbacks
        //[HttpGet]
        //public IEnumerable<string> Get()
        //{
        //    return new string[] { "value1", "value2" };
        //}

        // GET: api/Feedbacks/5
        //[HttpGet("{id}", Name = "Get")]
        //public string Get(int id)
        //{
        //    return "value";
        //}

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



            return StatusCode(201);
        }

        // PUT: api/Feedbacks/5
        //[HttpPut("{id}")]
        //public void Put(int id, [FromBody] string value)
        //{
        //}

        //// DELETE: api/ApiWithActions/5
        //[HttpDelete("{id}")]
        //public void Delete(int id)
        //{
        //}
    }
}
