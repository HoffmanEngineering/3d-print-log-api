using System;

namespace PrintLogApi.Services
{
    /// <summary>
    /// Utilities for helping with measurement conversions.
    /// </summary>
    public static class MeasurementUtilities
    {
        public static long GetAmountMgFromLength(double lengthInMeters, double filamentDiameterInMM, double materialDensityGramPerCubicCm)
        {
            return (long)Math.Round(250.0 * Math.PI * materialDensityGramPerCubicCm * filamentDiameterInMM * filamentDiameterInMM * lengthInMeters);
        }

        public static long GetAmountMgFromVolume(double VolumeInMl, double materialDensityGramPerCubicCm)
        {
            return (long)Math.Round(VolumeInMl * materialDensityGramPerCubicCm * 1000);
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
