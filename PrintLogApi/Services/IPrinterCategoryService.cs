using PrintLogApi.Models;

namespace PrintLogApi.Services;

public interface IPrinterCategoryService
{
    Task<bool> exists(string nickname);
    Task<PrinterCategory?> get(string nickname);
}
