using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.MaterialCategory;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Manage the list of material categories
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class MaterialCategoriesController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;

        public MaterialCategoriesController(PrintLogContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        /// <summary>
        /// Returns the current list of material types for the material categories dropdown.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any, NoStore = false)]
        public async Task<ActionResult<IEnumerable<MaterialCategoryDto>>> GetMaterials()
        {
            return await _context.MaterialCategories.ProjectTo<MaterialCategoryDto>(_mapper.ConfigurationProvider).OrderBy(m => m.Nickname).ToListAsync();
        }

    }
}
