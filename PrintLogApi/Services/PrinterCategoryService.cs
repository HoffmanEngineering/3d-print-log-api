using System.Linq;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;

namespace PrintLogApi.Services
{
    public class PrinterCategoryService: IPrinterCategoryService
    {

        private readonly PrintLogContext _context;
        private readonly TelemetryClient _telemetry;

        public PrinterCategoryService(PrintLogContext context, TelemetryClient telemetry)
        {
            _context = context;
            _telemetry = telemetry;
        }

        public async Task<PrinterCategory> get(string nickname)
        {
            return await _context.PrinterCategories
                .Where(p => p.Nickname == nickname)
                .SingleOrDefaultAsync();
        }

        public async Task<bool> exists(string nickname) {

            var exists = await _context.PrinterCategories.AnyAsync(p => p.Nickname == nickname);

            return exists;
        }


    }


}
