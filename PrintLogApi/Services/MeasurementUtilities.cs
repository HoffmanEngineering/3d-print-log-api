using System;

namespace PrintLogApi.Services
{
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
    }
}
