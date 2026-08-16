using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Materials;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Manage the list of default material types.
    /// </summary>
    [Route("api/Materials")]
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class MaterialTypesController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;

        public MaterialTypesController(PrintLogContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        /// <summary>
        /// Returns the current list of material types for the material selection dropdown.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<ActionResult<IEnumerable<MaterialTypeDto>>> GetMaterials()
        {
            return await _context.MaterialTypes.ProjectTo<MaterialTypeDto>(_mapper.ConfigurationProvider).OrderBy(m => m.Acronym).ToListAsync();
        }

    }
}
