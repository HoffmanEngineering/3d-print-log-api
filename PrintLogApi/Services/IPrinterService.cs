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
        Task<Printer?> getPrinterById(long printerId);
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

        /// <summary>
        /// Creates a printer for the MCP write surface. Ownership is token-derived (never an
        /// argument). The category must exist when provided — an unknown one is rejected, never
        /// silently replaced — and resolves to the default when omitted.
        /// <para>
        /// Deliberately NOT the <see cref="Models.DTOs.Printer.AddPrinterDTO"/> path: that map does
        /// not ignore LoadedFilaments/UserId/ids, so mapping a DTO over a Printer would clobber the
        /// loaded-filament collection. Scalars are patched directly and no PrinterFilament row is
        /// ever touched.
        /// </para>
        /// <para>
        /// <paramref name="idempotencyKey"/> is OPTIONAL: with a key, a retry carrying the same
        /// arguments replays the original printer and a key reused with different arguments is a
        /// conflict; without one, every call creates a new printer.
        /// </para>
        /// </summary>
        Task<CreatePrinterResult> CreatePrinterForMcp(
            long userId, PrinterAttributesInput input, string? idempotencyKey, CancellationToken ct);

        /// <summary>
        /// Edits one of the caller's own printers through a dedicated ownership-scoped path.
        /// <para>
        /// Deliberately NOT the PutPrinter path: that one maps a whole AddPrinterDTO over the tracked
        /// entity and calls setLoadedFilament, so it can add, unload and re-home PrinterFilament rows.
        /// This method patches scalars only and never loads the loaded-filament collection at all.
        /// </para>
        /// <para>
        /// Foreign or missing ids surface NotFound. Only fields present in <paramref name="input"/>
        /// change; fields named in <paramref name="clear"/> are nulled. Everything is validated before
        /// any mutation reaches the entity, so a rejected edit leaves the printer untouched.
        /// </para>
        /// </summary>
        Task<PrinterDetailResult> UpdatePrinterForMcp(
            long userId, long printerId, PrinterAttributesInput input, ISet<string>? clear, CancellationToken ct);
    }
}