using AutoMapper;
using PrintLogApi.Enums;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;

namespace PrintLogApi.Profiles;

public class FilamentProfile : Profile
{
    public FilamentProfile()
    {
        // Lightweight mapping for printer summary views - avoids expensive calculations
        CreateMap<Filament, FilamentSummaryForPrinterDto>()
            .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern ?? ColorPatternType.Solid))
            .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType ?? FilamentFinishType.Standard))
            .ForMember(dest => dest.Colors, src => src.MapFrom(src =>
                src.Colors != null && src.Colors.Count > 0 ? src.Colors : new List<string> { src.ColorHex! }))
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects ?? new List<FilamentEffect>()));

        CreateMap<Filament, FilamentSummaryDto>()
            .ForMember(dest => dest.LoadedInPrinter, src => src.MapFrom(src => src.PrinterFilaments!.Where(pf => !pf.UnloadedDateTime.HasValue).Select(p => p.Printer).FirstOrDefault()))
            // Volume and length are NOT mapped here: FilamentSummaryDto derives them from
            // this figure so the list, the detail page and each other cannot disagree.
            // Usage folded into milligrams, whatever measure the row was recorded in.
            // PrintService normalizes a saved row into all three measures, so AmountMg is
            // present for weight, length and volume sources alike - but a row written
            // around that path (seeded data, an import, a filament with no usable diameter
            // at save time) carries only what the user entered. Milligrams are the common
            // denominator every other figure here converts from.
            .ForMember(dest => dest.FilamentRemaining, src => src.MapFrom(src => (long?)Math.Round((src.InitialNominalWeightMg ?? 0)
                - src.PrintFilaments!.Sum(p => p.AmountMg.HasValue && p.AmountMg > 0 ? (double)p.AmountMg.Value
                : p.VolumeMl.HasValue && p.VolumeMl > 0 ? p.VolumeMl.Value * p.Filament!.MaterialDensityGramPerCubicCm * 1000.0
                : p.LengthInM.HasValue && p.LengthInM > 0 && p.Filament!.DiameterMm.HasValue && p.Filament.DiameterMm > 0
                    ? 250.0 * Math.PI * p.Filament!.MaterialDensityGramPerCubicCm * p.Filament.DiameterMm!.Value * p.Filament.DiameterMm!.Value * p.LengthInM.Value
                : p.EstimatedAmountMg.HasValue && p.EstimatedAmountMg > 0 ? (double)p.EstimatedAmountMg.Value
                : p.EstimatedVolumeMl.HasValue && p.EstimatedVolumeMl > 0 ? p.EstimatedVolumeMl.Value * p.Filament!.MaterialDensityGramPerCubicCm * 1000.0
                : p.EstimatedLengthInM.HasValue && p.EstimatedLengthInM > 0 && p.Filament!.DiameterMm.HasValue && p.Filament.DiameterMm > 0
                    ? 250.0 * Math.PI * p.Filament!.MaterialDensityGramPerCubicCm * p.Filament.DiameterMm!.Value * p.Filament.DiameterMm!.Value * p.EstimatedLengthInM.Value
                : 0.0)
                + src.FilamentAdjustments!.Sum(adj => (double)(adj.AmountMg ?? 0)))))
            .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern ?? ColorPatternType.Solid))
            .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType ?? FilamentFinishType.Standard))
            .ForMember(dest => dest.Colors, src => src.MapFrom(src =>
                src.Colors != null && src.Colors.Count > 0 ? src.Colors : new List<string> { src.ColorHex! }))
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects ?? new List<FilamentEffect>()))
            .ForMember(dest => dest.ColorHex, src => src.MapFrom(src =>
                src.Colors != null && src.Colors.Count > 0 ? src.Colors[0] : src.ColorHex));

        CreateMap<FilamentSummaryDto, Filament>();

        CreateMap<AddFilamentDto, Filament>()
            .ForMember(dest => dest.MaterialCategoryNickname, src => src.MapFrom(src => !String.IsNullOrEmpty(src.MaterialCategoryNickname) ? src.MaterialCategoryNickname : "filament"))
            .ForMember(dest => dest.Source, src => src.MapFrom(src => src.Source.HasValue ? src.Source : Filament.SourceMeasurement.Weight))
            .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern))
            .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType))
            .ForMember(dest => dest.Colors, src => src.MapFrom(src => src.Colors))
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects));

        CreateMap<EditFilamentDto, Filament>()
            .ForMember(dest => dest.MaterialCategoryNickname, src => src.MapFrom(src => !String.IsNullOrEmpty(src.MaterialCategoryNickname) ? src.MaterialCategoryNickname : "filament"))
            .ForMember(dest => dest.Source, src => src.MapFrom(src => src.Source.HasValue ? src.Source : Filament.SourceMeasurement.Weight))
            .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern))
            .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType))
            .ForMember(dest => dest.Colors, src => src.MapFrom(src => src.Colors))
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects));

        CreateMap<FilamentDetailDto, Filament>()
            .ForMember(dest => dest.Source, src => src.MapFrom(src => src.Source.HasValue ? src.Source : Filament.SourceMeasurement.Weight))
            .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern))
            .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType))
            .ForMember(dest => dest.Colors, src => src.MapFrom(src => src.Colors))
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects));

        CreateMap<Filament, FilamentDetailDto>()
            // Volume and length are NOT mapped here: FilamentDetailDto derives them from
            // this figure, which also gives them the untracked-spool null for free. Without
            // that an untracked spool would return remaining null alongside length 0 and
            // volume 0, and the UI would render "Not tracked" beside a contradictory "0.0 m".
            // Usage folded into milligrams, whatever measure the row was recorded in.
            // PrintService normalizes a saved row into all three measures, so AmountMg is
            // present for weight, length and volume sources alike - but a row written
            // around that path (seeded data, an import, a filament with no usable diameter
            // at save time) carries only what the user entered. Milligrams are the common
            // denominator every other figure here converts from.
            .ForMember(dest => dest.FilamentRemaining, src => src.MapFrom(src => src.InitialNominalWeightMg.HasValue
                ? (long?)Math.Round(src.InitialNominalWeightMg.Value
                - src.PrintFilaments!.Sum(p => p.AmountMg.HasValue && p.AmountMg > 0 ? (double)p.AmountMg.Value
                : p.VolumeMl.HasValue && p.VolumeMl > 0 ? p.VolumeMl.Value * p.Filament!.MaterialDensityGramPerCubicCm * 1000.0
                : p.LengthInM.HasValue && p.LengthInM > 0 && p.Filament!.DiameterMm.HasValue && p.Filament.DiameterMm > 0
                    ? 250.0 * Math.PI * p.Filament!.MaterialDensityGramPerCubicCm * p.Filament.DiameterMm!.Value * p.Filament.DiameterMm!.Value * p.LengthInM.Value
                : p.EstimatedAmountMg.HasValue && p.EstimatedAmountMg > 0 ? (double)p.EstimatedAmountMg.Value
                : p.EstimatedVolumeMl.HasValue && p.EstimatedVolumeMl > 0 ? p.EstimatedVolumeMl.Value * p.Filament!.MaterialDensityGramPerCubicCm * 1000.0
                : p.EstimatedLengthInM.HasValue && p.EstimatedLengthInM > 0 && p.Filament!.DiameterMm.HasValue && p.Filament.DiameterMm > 0
                    ? 250.0 * Math.PI * p.Filament!.MaterialDensityGramPerCubicCm * p.Filament.DiameterMm!.Value * p.Filament.DiameterMm!.Value * p.EstimatedLengthInM.Value
                : 0.0)
                + src.FilamentAdjustments!.Sum(adj => (double)(adj.AmountMg ?? 0)))
                : (long?)null))
            // Distinct prints, not usage rows: there is no unique index on (PrintId, FilamentId),
            // so one print may hold two rows for the same spool.
            //
            // Read PrintId, never p.Print. GetFilamentById Includes PrintFilaments but not the
            // Print behind each one, and there is no lazy-loading proxy, so reaching through that
            // navigation here would NullReferenceException on every filament that has usage.
            .ForMember(dest => dest.PrintCount, src => src.MapFrom(src => src.PrintFilaments!.Select(p => p.PrintId).Distinct().Count()))
            .ForMember(dest => dest.TotalUsedMg, src => src.MapFrom(src => (long)Math.Round(src.PrintFilaments!.Sum(p => p.AmountMg.HasValue && p.AmountMg > 0 ? (double)p.AmountMg.Value
                : p.VolumeMl.HasValue && p.VolumeMl > 0 ? p.VolumeMl.Value * p.Filament!.MaterialDensityGramPerCubicCm * 1000.0
                : p.LengthInM.HasValue && p.LengthInM > 0 && p.Filament!.DiameterMm.HasValue && p.Filament.DiameterMm > 0
                    ? 250.0 * Math.PI * p.Filament!.MaterialDensityGramPerCubicCm * p.Filament.DiameterMm!.Value * p.Filament.DiameterMm!.Value * p.LengthInM.Value
                : p.EstimatedAmountMg.HasValue && p.EstimatedAmountMg > 0 ? (double)p.EstimatedAmountMg.Value
                : p.EstimatedVolumeMl.HasValue && p.EstimatedVolumeMl > 0 ? p.EstimatedVolumeMl.Value * p.Filament!.MaterialDensityGramPerCubicCm * 1000.0
                : p.EstimatedLengthInM.HasValue && p.EstimatedLengthInM > 0 && p.Filament!.DiameterMm.HasValue && p.Filament.DiameterMm > 0
                    ? 250.0 * Math.PI * p.Filament!.MaterialDensityGramPerCubicCm * p.Filament.DiameterMm!.Value * p.Filament.DiameterMm!.Value * p.EstimatedLengthInM.Value
                : 0.0))))
            .ForMember(dest => dest.ColorPattern, src => src.MapFrom(src => src.ColorPattern ?? ColorPatternType.Solid))
            .ForMember(dest => dest.FinishType, src => src.MapFrom(src => src.FinishType ?? FilamentFinishType.Standard))
            .ForMember(dest => dest.Colors, src => src.MapFrom(src =>
                src.Colors != null && src.Colors.Count > 0 ? src.Colors : new List<string> { src.ColorHex! }))
            .ForMember(dest => dest.Effects, src => src.MapFrom(src => src.Effects ?? new List<FilamentEffect>()))
            .ForMember(dest => dest.ColorHex, src => src.MapFrom(src =>
                src.Colors != null && src.Colors.Count > 0 ? src.Colors[0] : src.ColorHex));

        CreateMap<FilamentAdjustment, FilamentAdjustmentDto>();

        CreateMap<FilamentAdjustmentDto, FilamentAdjustment>()
            .ForMember(dest => dest.Source, src => src.MapFrom(src => src.Source.HasValue ? src.Source : FilamentAdjustment.SourceMeasurement.Weight));
    }
}
