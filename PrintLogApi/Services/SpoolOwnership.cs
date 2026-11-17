using System.Linq.Expressions;
using PrintLogApi.Models;

namespace PrintLogApi.Services;

/// <summary>
/// Which links to a spool count. A spool belongs to its creator (<c>Filament.CreatedById</c>), a
/// printer to <c>Printer.UserId</c>, and every write path checks that a spool it links is the
/// caller's. A row written before that check existed, or around it, can still name somebody
/// else's spool; these rules make such a row inert everywhere it is read, so it can neither show
/// that spool to the printer's owner nor change the spool's figures for its own owner.
///
/// <para>Each rule compares owner ids through the row's own navigations, so spliced into a
/// correlated aggregate (<see cref="Profiles.FilamentProfile"/>) it adds joins and a WHERE, never
/// an outer reference inside the aggregate (SQL Server error 8124). In memory the navigations must
/// be loaded; a missing one makes a row not count, which is the safe direction.</para>
/// </summary>
public static class SpoolOwnership
{
    /// <summary>
    /// A loaded-spool row counts only when the spool's owner owns the printer: on the printer's
    /// side (its detail, summary and loaded list, the spool a print event charges) and on the
    /// spool's side (where it is loaded).
    /// </summary>
    public static readonly Expression<Func<PrinterFilament, bool>> LoadedRowCounts = pf =>
        pf.Filament != null && pf.Printer != null && pf.Filament.CreatedById == pf.Printer.UserId;

    /// <summary>
    /// A usage row counts toward its spool only when the spool's owner created the print or owns
    /// the print's printer. The second half keeps older prints that sit on another user's printer:
    /// that printer's owner may edit them (PrintsController.PutPrint) and link their own spools.
    /// </summary>
    public static readonly Expression<Func<PrintFilament, bool>> UsageRowCounts = pf =>
        pf.Filament != null
        && pf.Print != null
        && (pf.Print.CreatedById == pf.Filament.CreatedById
            || (pf.Print.Printer != null && pf.Print.Printer.UserId == pf.Filament.CreatedById));

    private static readonly Func<PrinterFilament, bool> LoadedRowCountsCompiled = LoadedRowCounts.Compile();

    private static readonly Func<PrintFilament, bool> UsageRowCountsCompiled = UsageRowCounts.Compile();

    /// <summary><see cref="LoadedRowCounts"/> in memory. Needs the row's Filament and Printer.</summary>
    public static bool Counts(PrinterFilament row) => LoadedRowCountsCompiled(row);

    /// <summary><see cref="UsageRowCounts"/> in memory. Needs the row's Filament, Print and the print's Printer.</summary>
    public static bool Counts(PrintFilament row) => UsageRowCountsCompiled(row);
}
