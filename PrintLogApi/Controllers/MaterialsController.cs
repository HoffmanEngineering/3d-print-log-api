using System.Collections.Generic;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Materials;
using System.Linq;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Manage the list of default filament material types.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class MaterialsController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;

        public MaterialsController(PrintLogContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        /// <summary>
        /// Returns the current list of material types for the filament selection dropdown.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<ActionResult<IEnumerable<MaterialDto>>> GetMaterials()
        {
            return await _context.Materials.ProjectTo<MaterialDto>(_mapper.ConfigurationProvider).OrderBy(m => m.Acronym).ToListAsync();
        }

    }
}
