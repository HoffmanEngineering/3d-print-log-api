using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;

namespace PrintLogApi.Services;

public class PrinterCategoryService(PrintLogContext context) : IPrinterCategoryService
{
    public async Task<PrinterCategory?> get(string nickname)
    {
        return await context.PrinterCategories
            .Where(p => p.Nickname == nickname)
            .SingleOrDefaultAsync();
    }

    public async Task<bool> exists(string nickname)
    {

        var exists = await context.PrinterCategories.AnyAsync(p => p.Nickname == nickname);

        return exists;
    }


}
