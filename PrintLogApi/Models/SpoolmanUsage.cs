using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// What Moonraker's <c>PUT /spoolman/api/v1/spool/{id}/use</c> reports have added up to for one
/// spool during one notifier print (#171). One row per (print, spool), incremented in place.
/// Transient: the notifier's finish turns these into the print's usage rows.
///
/// <para>No FK to the filament, on purpose: a spool deleted mid-print must not fail the report or
/// cascade through here, and the finish joins on it and skips what no longer exists. Not a
/// TimestampEntity: there is no "updated by" for a printer's report, and its two required FKs to
/// Users would be a second cascade path alongside the print's (SQL Server error 1785).</para>
/// </summary>
public class SpoolmanUsage
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public long PrintId { get; set; }

    [ForeignKey(nameof(PrintId))]
    public Print Print { get; set; } = null!;

    /// <summary>The spool's <see cref="Filament.Id"/>. Not a foreign key; see the class.</summary>
    public Guid FilamentId { get; set; }

    /// <summary>
    /// The sum of the reported lengths, in millimetres. Moonraker can "un-use", so a report may be
    /// negative and so may this; the finish records a negative total as 0.
    /// </summary>
    public double LengthMm { get; set; }

    /// <summary>When the last report landed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
