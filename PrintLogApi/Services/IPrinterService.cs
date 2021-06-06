using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PrintLogApi.Models;

namespace PrintLogApi.Services
{
    public interface IPrinterService
    {
        Task<Printer> getPrinterById(long printerId);
        Task setLoadedFilament(long printerId, IEnumerable<Guid> loadedFilamentIds);
    }
}