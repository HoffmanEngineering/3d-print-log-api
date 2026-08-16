using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.PrinterCategory;

namespace PrintLogApi.Controllers;

/// <summary>
/// Manage the list of printer categories
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class PrinterCategoriesController(PrintLogContext context, IMapper mapper) : ControllerBase
{
    /// <summary>
    /// Returns the current list of printer categories for the material selection dropdown.
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any, NoStore = false)]
    public async Task<ActionResult<IEnumerable<PrinterCategoryDto>>> GetMaterials()
    {
        return await context.PrinterCategories.ProjectTo<PrinterCategoryDto>(mapper.ConfigurationProvider).OrderBy(m => m.Nickname).ToListAsync();
    }

}
