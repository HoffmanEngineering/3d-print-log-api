using PrintLogApi.Models;

namespace PrintLogApi.Services;

/// <summary>
/// Utilities for helping with measurement conversions.
/// </summary>
public static class MeasurementUtilities
{
    /// <summary>
    /// The length->milligram formula, unrounded and uncast. Callers that must reject an
    /// out-of-range result (the MCP write surface) need the raw double: the long-returning
    /// overload casts UNCHECKED and would wrap silently.
    /// </summary>
    public static double GetAmountMgFromLengthUnrounded(double lengthInMeters, double filamentDiameterInMM, double materialDensityGramPerCubicCm)
    {
        return 250.0 * Math.PI * materialDensityGramPerCubicCm * filamentDiameterInMM * filamentDiameterInMM * lengthInMeters;
    }

    public static long GetAmountMgFromLength(double lengthInMeters, double filamentDiameterInMM, double materialDensityGramPerCubicCm)
    {
        return (long)Math.Round(GetAmountMgFromLengthUnrounded(lengthInMeters, filamentDiameterInMM, materialDensityGramPerCubicCm));
    }

    /// <summary>The volume->milligram formula, unrounded and uncast. See the length overload.</summary>
    public static double GetAmountMgFromVolumeUnrounded(double VolumeInMl, double materialDensityGramPerCubicCm)
    {
        return VolumeInMl * materialDensityGramPerCubicCm * 1000;
    }

    public static long GetAmountMgFromVolume(double VolumeInMl, double materialDensityGramPerCubicCm)
    {
        return (long)Math.Round(GetAmountMgFromVolumeUnrounded(VolumeInMl, materialDensityGramPerCubicCm));
    }

    /// <summary>
    /// Converts between an amount in milligrams to the expected length of that filament in meters.
    /// </summary>
    /// <returns></returns>
    public static double GetLengthInMetersFromAmount(long amountMg, double filamentDiameterInMM, double materialDensityGramPerCubicCm)
    {
        return ((double)amountMg) / ((250.0 * Math.PI * materialDensityGramPerCubicCm * filamentDiameterInMM * filamentDiameterInMM));
    }

    public static double GetLengthInMetersFromVolume(double volumeMl, double filamentDiameterInMM)
    {
        return volumeMl / ((1 / 4.00) * Math.PI * filamentDiameterInMM * filamentDiameterInMM); ;
    }

    /// <summary>
    /// Returns the Volume from a weight
    /// </summary>
    public static double GetVolumeInMlFromAmount(long amountMg, double materialDensityGramPerCubicCm)
    {
        return (amountMg) / (1000.0 * materialDensityGramPerCubicCm);
    }

    /// <summary>
    /// Converts between a filament length in meters into a volume in milliliters
    /// </summary>
    public static double GetVolumeInMlFromLengthM(double lengthInMeters, double filamentDiameterInMM)
    {
        return ((1 / 4.00) * Math.PI * lengthInMeters * filamentDiameterInMM * filamentDiameterInMM);
    }

    /// <summary>
    /// A derived weight narrowed to the <c>int</c> column. Clamped because an unchecked
    /// long-to-int cast wraps, storing a huge length as a negative weight (#198).
    /// </summary>
    public static int ToIntMg(long mg) => (int)Math.Clamp(mg, int.MinValue, int.MaxValue);

    /// <summary>
    /// Fills in a usage row's derived measures from the one each side names as its source, using
    /// the spool's density and diameter. The actual and the estimated side are converted
    /// independently. A side recorded by weight keeps its AmountMg and only its volume and length
    /// are derived; a side recorded by length or volume gets its AmountMg (the figure remaining
    /// is computed from) re-derived.
    /// <para>
    /// The one copy of the rule: the print write paths run it on save, and a spool edit that
    /// changes the density, diameter or category runs it over that spool's rows, so a corrected
    /// density cannot leave old prints charged at the old one. A spool that can't convert (no
    /// density, or a diameter-tracking category without a diameter) leaves the row as it is.
    /// </para>
    /// </summary>
    public static void DeriveUsageMeasurements(PrintFilament pf, Filament filament)
    {
        bool hasDiameter = filament.MaterialCategory?.HasDiameter == true;

        if (filament.MaterialDensityGramPerCubicCm <= 0
            || (hasDiameter && (!filament.DiameterMm.HasValue || filament.DiameterMm <= 0)))
        {
            return;
        }

        if (pf.Source == PrintFilament.SourceMeasurement.Length)
        {
            // The diameter test is what the Volume and Weight branches below already do.
            // Without it a material whose category tracks no diameter (resin, powder)
            // reaches DiameterMm.Value and throws.
            if (pf.LengthInM.HasValue && hasDiameter && filament.DiameterMm is { } diameterMm)
            {
                pf.AmountMg = ToIntMg(GetAmountMgFromLength(pf.LengthInM.Value, diameterMm, filament.MaterialDensityGramPerCubicCm));
                pf.VolumeMl = GetVolumeInMlFromLengthM(pf.LengthInM.Value, diameterMm);
            }
        }
        else if (pf.Source == PrintFilament.SourceMeasurement.Volume)
        {
            if (pf.VolumeMl.HasValue)
            {
                pf.AmountMg = ToIntMg(GetAmountMgFromVolume(pf.VolumeMl.Value, filament.MaterialDensityGramPerCubicCm));

                if (hasDiameter && filament.DiameterMm is { } diameterMm)
                {
                    pf.LengthInM = GetLengthInMetersFromVolume(pf.VolumeMl.Value, diameterMm);
                }
            }
        }
        else
        {
            if (pf.AmountMg.HasValue)
            {
                pf.VolumeMl = GetVolumeInMlFromAmount(pf.AmountMg.Value, filament.MaterialDensityGramPerCubicCm);

                if (hasDiameter && filament.DiameterMm is { } diameterMm)
                {
                    pf.LengthInM = GetLengthInMetersFromAmount(pf.AmountMg.Value, diameterMm, filament.MaterialDensityGramPerCubicCm);
                }
            }
        }

        if (pf.EstimatedSource == PrintFilament.SourceMeasurement.Length)
        {
            // Same missing diameter test as the actual-measurement Length branch above.
            if (pf.EstimatedLengthInM.HasValue && hasDiameter && filament.DiameterMm is { } diameterMm)
            {
                pf.EstimatedAmountMg = ToIntMg(GetAmountMgFromLength(pf.EstimatedLengthInM.Value, diameterMm, filament.MaterialDensityGramPerCubicCm));
                pf.EstimatedVolumeMl = GetVolumeInMlFromLengthM(pf.EstimatedLengthInM.Value, diameterMm);
            }
        }
        else if (pf.EstimatedSource == PrintFilament.SourceMeasurement.Volume)
        {
            if (pf.EstimatedVolumeMl.HasValue)
            {
                pf.EstimatedAmountMg = ToIntMg(GetAmountMgFromVolume(pf.EstimatedVolumeMl.Value, filament.MaterialDensityGramPerCubicCm));

                if (hasDiameter && filament.DiameterMm is { } diameterMm)
                {
                    pf.EstimatedLengthInM = GetLengthInMetersFromVolume(pf.EstimatedVolumeMl.Value, diameterMm);
                }
            }
        }
        else
        {
            if (pf.EstimatedAmountMg.HasValue)
            {
                pf.EstimatedVolumeMl = GetVolumeInMlFromAmount(pf.EstimatedAmountMg.Value, filament.MaterialDensityGramPerCubicCm);

                if (hasDiameter && filament.DiameterMm is { } diameterMm)
                {
                    pf.EstimatedLengthInM = GetLengthInMetersFromAmount(pf.EstimatedAmountMg.Value, diameterMm, filament.MaterialDensityGramPerCubicCm);
                }
            }
        }
    }

    /// <summary>
    /// Remaining volume for a spool, derived from its remaining weight so the weight,
    /// volume and length readouts can never disagree with one another. Null when the
    /// spool is untracked, or when the material carries no usable density to convert with.
    /// </summary>
    public static double? GetVolumeRemainingInMl(long? remainingMg, double materialDensityGramPerCubicCm)
    {
        if (!remainingMg.HasValue || materialDensityGramPerCubicCm <= 0)
        {
            return null;
        }

        return GetVolumeInMlFromAmount(remainingMg.Value, materialDensityGramPerCubicCm);
    }

    /// <summary>
    /// Remaining length for a spool, derived from its remaining weight. Null when the spool
    /// is untracked, or when the material has no diameter to convert with - resins and
    /// powders have no length, and reporting 0 there reads as an empty spool.
    /// </summary>
    public static double? GetLengthRemainingInM(long? remainingMg, double? diameterMm, double materialDensityGramPerCubicCm)
    {
        if (!remainingMg.HasValue || materialDensityGramPerCubicCm <= 0 || diameterMm is not > 0)
        {
            return null;
        }

        return GetLengthInMetersFromAmount(remainingMg.Value, diameterMm.Value, materialDensityGramPerCubicCm);
    }
}
