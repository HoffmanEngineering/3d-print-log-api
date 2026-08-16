using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.PrinterCategory;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Manage the list of printer categories
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class PrinterCategoriesController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;

        public PrinterCategoriesController(PrintLogContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        /// <summary>
        /// Returns the current list of printer categories for the material selection dropdown.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any, NoStore = false)]
        public async Task<ActionResult<IEnumerable<PrinterCategoryDto>>> GetMaterials()
        {
            return await _context.PrinterCategories.ProjectTo<PrinterCategoryDto>(_mapper.ConfigurationProvider).OrderBy(m => m.Nickname).ToListAsync();
        }

    }
}
