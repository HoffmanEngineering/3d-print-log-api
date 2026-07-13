using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PrintLogApi.Mcp;
using PrintLogApi.Models;

namespace PrintLogApi.Services
{
    public interface IPrinterService
    {
        Task DeletePrinter(long printerId);
        Task<Printer> getPrinterById(long printerId);
        Task setLoadedFilament(long printerId, IEnumerable<Guid> loadedFilamentIds);

        /// <summary>
        /// The caller's printers, paginated. Lets an agent resolve a printer by name to an id;
        /// previously printer names only ever leaked out embedded in print results.
        /// </summary>
        Task<McpPage<PrinterListItem>> ListPrintersForMcp(
            long userId, int page, int pageSize, CancellationToken ct);

        /// <summary>
        /// Full detail for one of the caller's printers, including the spools currently loaded on
        /// it. Creator-only: a foreign id is not-found, with no existence oracle.
        /// </summary>
        Task<PrinterDetailResult> GetPrinterForMcp(long userId, long printerId, CancellationToken ct);
    }
}