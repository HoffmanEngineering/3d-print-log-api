using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.MaterialCategory;

namespace PrintLogApi.Controllers;

/// <summary>
/// Manage the list of material categories
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class MaterialCategoriesController(PrintLogContext context, IMapper mapper) : ControllerBase
{
    /// <summary>
    /// Returns the current list of material types for the material categories dropdown.
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any, NoStore = false)]
    public async Task<ActionResult<IEnumerable<MaterialCategoryDto>>> GetMaterials()
    {
        return await context.MaterialCategories.ProjectTo<MaterialCategoryDto>(mapper.ConfigurationProvider).OrderBy(m => m.Nickname).ToListAsync();
    }

}
